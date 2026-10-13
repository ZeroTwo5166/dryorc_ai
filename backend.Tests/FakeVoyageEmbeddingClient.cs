using System.Security.Cryptography;
using System.Text;
using backend.Constants;
using backend.Enums;
using backend.Services;

namespace backend.Tests;

/// <summary>
/// Registered in place of the real Voyage-backed IVoyageEmbeddingClient for
/// normal (non-live) test execution — see NivoraApiFactory. Returns
/// deterministic, genuinely 1024-dimensional vectors derived from each input
/// text's own SHA-256 hash (never GetHashCode — documented by .NET as
/// unstable across process runs, same reasoning AdvisoryLockKeyGenerator's
/// own doc comment gives), so the same text always produces the same vector
/// and different texts produce different vectors — lets tests assert "this
/// chunk's embedding corresponds to this chunk's text" without needing real
/// Voyage semantics.
///
/// A1 Slice 3: also implements EmbedWithUsageAsync, filling in the usage
/// accumulator per simulated batch (otherwise every IVoyageEmbeddingInvoker
/// row under NivoraApiFactory would carry null usage — Slice 3 design pass
/// §6). Batches are simulated in slices of <see cref="BatchSize"/>, each
/// reporting <see cref="UsageForBatch"/>. <see cref="ExceptionToThrow"/> is
/// thrown when batch index <see cref="FailAtBatch"/> is reached (default 0:
/// before any batch, the pre-Slice 3 behavior), after the earlier batches
/// have been added to the accumulator. <see cref="OnFailing"/> runs just
/// before that throw (e.g. to cancel the caller's token mid-call).
/// </summary>
public class FakeVoyageEmbeddingClient : IVoyageEmbeddingClient
{
    public const int Dimension = 1024;

    public Exception? ExceptionToThrow { get; set; }
    public int FailAtBatch { get; set; }
    public Action? OnFailing { get; set; }
    public int BatchSize { get; set; } = VoyageModels.MaxTextsPerRequest;
    public Func<IReadOnlyList<string>, long?> UsageForBatch { get; set; } = batch => batch.Count;
    public int CallCount { get; private set; }
    public List<(IReadOnlyList<string> Texts, VoyageInputType InputType)> Requests { get; } = [];

    public Task<IReadOnlyList<float[]>> EmbedWithUsageAsync(
        IReadOnlyList<string> texts, VoyageInputType inputType, VoyageUsageAccumulator usage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(usage);

        CallCount++;
        Requests.Add((texts, inputType));

        var batches = texts.Chunk(BatchSize).ToList();
        usage.SetRequestedBatches(batches.Count);

        for (var i = 0; i <= batches.Count; i++)
        {
            if (ExceptionToThrow is not null && i == FailAtBatch)
            {
                OnFailing?.Invoke();
                throw ExceptionToThrow;
            }
            if (i < batches.Count)
            {
                usage.AddCompletedBatch(UsageForBatch(batches[i]));
            }
        }

        IReadOnlyList<float[]> results = texts.Select(DeterministicVector).ToList();
        return Task.FromResult(results);
    }

    // Public so other test files (e.g. KnowledgeRetrievalServiceTests) can
    // precompute the exact vector this fake will produce for a given text,
    // without duplicating the algorithm.
    public static float[] DeterministicVector(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var seed = BitConverter.ToInt32(hash, 0);
        var random = new Random(seed);

        var vector = new float[Dimension];
        for (var i = 0; i < Dimension; i++)
        {
            vector[i] = (float)(random.NextDouble() * 2 - 1);
        }
        return vector;
    }
}
