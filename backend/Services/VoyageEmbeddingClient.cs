using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using backend.Constants;
using backend.Enums;

namespace backend.Services;

/// <summary>
/// Real Voyage AI embeddings client — POST https://api.voyageai.com/v1/embeddings,
/// Bearer auth (see Program.cs's DI registration for the ApiKey wiring).
/// Never sends output_dimension: voyage-3 (the only model this app ever
/// requests, VoyageModels.Voyage3) does not support that parameter — see
/// VoyageModels' own doc comment — and its native output is already exactly
/// 1024-dim, matching KnowledgeChunk.Embedding's fixed vector(1024) column.
/// </summary>
public class VoyageEmbeddingClient(HttpClient httpClient) : IVoyageEmbeddingClient
{
    public async Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> texts, VoyageInputType inputType, CancellationToken cancellationToken = default)
    {
        if (texts.Count == 0)
        {
            return [];
        }

        var results = new float[texts.Count][];
        var offset = 0;

        // Sequential, not parallel — no retry/backoff for MVP, so avoiding
        // concurrent requests keeps this simple and avoids any real risk of
        // tripping a rate limit across simultaneous requests.
        while (offset < texts.Count)
        {
            var batchSize = Math.Min(VoyageModels.MaxTextsPerRequest, texts.Count - offset);
            var batch = texts.Skip(offset).Take(batchSize).ToList();
            var batchResults = await EmbedBatchAsync(batch, inputType, cancellationToken);

            for (var i = 0; i < batch.Count; i++)
            {
                results[offset + i] = batchResults[i];
            }

            offset += batch.Count;
        }

        return results;
    }

    private async Task<float[][]> EmbedBatchAsync(
        IReadOnlyList<string> batch, VoyageInputType inputType, CancellationToken cancellationToken)
    {
        var requestBody = new VoyageEmbeddingRequest
        {
            Input = batch,
            Model = VoyageModels.Voyage3,
            InputType = inputType == VoyageInputType.Query ? "query" : "document",
        };

        HttpResponseMessage response;
        try
        {
            response = await httpClient.PostAsJsonAsync("v1/embeddings", requestBody, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new VoyageEmbeddingException("Failed to reach the Voyage embeddings API.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new VoyageEmbeddingException("Voyage embeddings request timed out.", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new VoyageEmbeddingException(
                $"Voyage embeddings request failed with status {(int)response.StatusCode}: {body}");
        }

        VoyageEmbeddingResponse? parsed;
        try
        {
            parsed = await response.Content.ReadFromJsonAsync<VoyageEmbeddingResponse>(cancellationToken: cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new VoyageEmbeddingException("Voyage embeddings response could not be parsed.", ex);
        }

        if (parsed?.Data is null)
        {
            throw new VoyageEmbeddingException("Voyage embeddings response was empty or malformed.");
        }

        // Map by Voyage's own returned index, never by array position —
        // Voyage's docs give no guarantee that response ordering matches
        // request ordering, and only describe "index" as identifying which
        // input an embedding belongs to. Fail loudly on anything unexpected
        // rather than risk silently pairing an embedding with the wrong chunk.
        var mapped = new float[batch.Count][];
        var seenIndexes = new HashSet<int>();

        foreach (var item in parsed.Data)
        {
            if (item.Index < 0 || item.Index >= batch.Count)
            {
                throw new VoyageEmbeddingException(
                    $"Voyage returned an out-of-range embedding index {item.Index} for a batch of {batch.Count} texts.");
            }
            if (!seenIndexes.Add(item.Index))
            {
                throw new VoyageEmbeddingException($"Voyage returned a duplicate embedding index {item.Index}.");
            }
            mapped[item.Index] = item.Embedding;
        }

        if (seenIndexes.Count != batch.Count)
        {
            throw new VoyageEmbeddingException(
                $"Voyage returned {seenIndexes.Count} embeddings for a batch of {batch.Count} texts — expected one per input.");
        }

        return mapped;
    }

    private class VoyageEmbeddingRequest
    {
        [JsonPropertyName("input")]
        public required IReadOnlyList<string> Input { get; init; }

        [JsonPropertyName("model")]
        public required string Model { get; init; }

        // Deliberately no output_dimension property at all — see this
        // class's containing type's doc comment.
        [JsonPropertyName("input_type")]
        public required string InputType { get; init; }
    }

    private class VoyageEmbeddingResponse
    {
        [JsonPropertyName("data")]
        public List<VoyageEmbeddingDatum>? Data { get; init; }
    }

    private class VoyageEmbeddingDatum
    {
        [JsonPropertyName("embedding")]
        public required float[] Embedding { get; init; }

        [JsonPropertyName("index")]
        public required int Index { get; init; }
    }
}
