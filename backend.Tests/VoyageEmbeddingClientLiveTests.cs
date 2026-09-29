using System.Net.Http.Headers;
using backend.Constants;
using backend.Enums;
using backend.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace backend.Tests;

/// <summary>
/// The one place in the suite that makes a real, billed Voyage API call —
/// follow-up research item V-Q2 (errata 3 §5.6): whether a real voyage-3
/// embeddings response carries a usable usage.total_tokens. Gated behind
/// [LiveVoyageFact], so it only runs when Voyage:EnableLiveIntegrationTests=true
/// AND a real Voyage:ApiKey are both configured. No DI, no database, no app
/// factory — just the real VoyageEmbeddingClient over a real HttpClient.
/// </summary>
public class VoyageEmbeddingClientLiveTests
{
    /// <summary>
    /// What a pass proves: a usable, non-negative integer usage.total_tokens
    /// greater than 0 was observed on ONE successful voyage-3 query response
    /// for this specific sentence, read through the production parse path
    /// (VoyageEmbeddingClient.ReadTotalTokens into VoyageUsageAccumulator).
    ///
    /// What a pass does NOT prove: V-Q2 items 2 (the full integer range),
    /// 3 (never absent or null on a 2xx) and 4 (other usage fields) from
    /// docs/handoffs/billing-usage-a1-slice3-design-pass.md:210-214 remain
    /// open, and input_type "document" is not covered. It also does not
    /// establish that every non-empty input yields usage.total_tokens > 0.
    /// </summary>
    [LiveVoyageFact]
    public async Task EmbedWithUsageAsync_RealApiCall_ReportsUsableTotalTokens()
    {
        var configuration = new ConfigurationBuilder().AddUserSecrets<Program>().Build();
        var apiKey = configuration["Voyage:ApiKey"]!; // LiveVoyageFact already guarantees this is present

        // Duplicates Program.cs's IVoyageEmbeddingClient HttpClient
        // configuration (BaseAddress + Bearer auth) — must be kept in sync
        // with it.
        using var httpClient = new HttpClient { BaseAddress = new Uri("https://api.voyageai.com/") };
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        var client = new VoyageEmbeddingClient(httpClient);
        var acc = new VoyageUsageAccumulator();

        // Very short query input such as "hello" has been observed to return
        // usage.total_tokens = 0 from voyage-3, so this test deliberately uses
        // a longer sentence. The cause of the 0 is not established.
        const string input = "The quick brown fox jumps over the lazy dog near the river bank.";

        IReadOnlyList<float[]>? result = null;
        var exception = await Record.ExceptionAsync(async () =>
            result = await client.EmbedWithUsageAsync([input], VoyageInputType.Query, acc));

        Assert.True(exception is null,
            $"The real Voyage embeddings call threw instead of completing: {exception}");

        Assert.True(result!.Count == 1,
            $"Expected exactly 1 embedding vector for 1 input text, got {result.Count}.");

        Assert.True(result[0].Length == EmbeddingDimensions.VoyageThree,
            $"Expected embedding length {EmbeddingDimensions.VoyageThree}, got {result[0].Length}. This is the " +
            "locked project dimension (HNSW index / KnowledgeChunk.Embedding) — a separate question from the " +
            "V-Q2 usage-field question.");

        Assert.True(acc.RequestedBatches == 1 && acc.CompletedBatches == 1,
            $"Expected exactly 1 requested and 1 completed batch, got RequestedBatches={acc.RequestedBatches}, " +
            $"CompletedBatches={acc.CompletedBatches}.");

        Assert.True(acc.TotalTokens is > 0,
            acc.TotalTokens is null
                ? "The client surfaced no usable usage.total_tokens. The provider's response shape is UNDETERMINED " +
                  "(the accumulator maps missing, non-numeric, fractional, negative and out-of-range values to null). " +
                  "V-Q2 is NOT confirmed."
                : $"usage.total_tokens was {acc.TotalTokens}, expected > 0 for this ordinary sentence input; a 0 has " +
                  "been observed by manual raw calls for very short query input such as \"hello\", and its cause is " +
                  "not established; V-Q2 is NOT confirmed by this run.");
    }
}
