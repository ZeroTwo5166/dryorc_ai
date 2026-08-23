using System.Security.Cryptography;
using System.Text;
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
/// </summary>
public class FakeVoyageEmbeddingClient : IVoyageEmbeddingClient
{
    public const int Dimension = 1024;

    public Exception? ExceptionToThrow { get; set; }
    public int CallCount { get; private set; }
    public List<(IReadOnlyList<string> Texts, VoyageInputType InputType)> Requests { get; } = [];

    public Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> texts, VoyageInputType inputType, CancellationToken cancellationToken = default)
    {
        CallCount++;
        Requests.Add((texts, inputType));

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
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
