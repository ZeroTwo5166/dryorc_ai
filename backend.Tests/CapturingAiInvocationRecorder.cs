using backend.Services;

namespace backend.Tests;

/// <summary>
/// In-memory IAiInvocationRecorder for the A1 Slice 3 wrapper unit tests
/// (AnthropicInvokerTests, VoyageEmbeddingInvokerTests). Captures every
/// request. <see cref="ExceptionToThrow"/> simulates a recorder that
/// breaks its never-throws contract; <see cref="Gate"/>, when set, holds
/// the returned task open until the test completes it (the fire-and-forget
/// tests T-A10/T-I9).
/// </summary>
public sealed class CapturingAiInvocationRecorder : IAiInvocationRecorder
{
    public List<AiInvocationRecordRequest> Records { get; } = [];
    public Exception? ExceptionToThrow { get; set; }
    public TaskCompletionSource? Gate { get; set; }

    public async Task RecordAsync(AiInvocationRecordRequest request)
    {
        Records.Add(request);

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        if (Gate is not null)
        {
            await Gate.Task;
        }
    }
}
