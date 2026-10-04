using System.Text.Json;
using backend.Services;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Test-only, non-mutating fake write tool — not a real write tool, never
/// touches Shopify or the database. Exists only to exercise WriteToolBase's
/// structural authorization enforcement (Decision 18).
/// </summary>
public class FakeWriteTool : WriteToolBase
{
    public bool AuthorizationResultToReturn { get; set; } = true;
    public string DenialMessage { get; set; } = "denied";
    public int AuthorizeCallCount { get; private set; }
    public int MutationCallCount { get; private set; }
    public string MutationResultContent { get; set; } = "mutated";
    public string? LastIdempotencyKey { get; private set; }

    public override string Name => "fakeWriteTool";
    public override string Description => "Test-only fake write tool.";
    public override Anthropic.Models.Messages.InputSchema InputSchema => new();

    public override IdempotencyToolInputs DescribeIdempotencyInputs(IReadOnlyDictionary<string, JsonElement> input) =>
        new("resource-1", "canonical-args");

    protected override Task<ToolAuthorizationResult> AuthorizeAsync(
        ToolDispatchContext context, IReadOnlyDictionary<string, JsonElement> input, CancellationToken cancellationToken)
    {
        AuthorizeCallCount++;
        return Task.FromResult(AuthorizationResultToReturn
            ? ToolAuthorizationResult.Authorized()
            : ToolAuthorizationResult.Denied(DenialMessage));
    }

    protected override Task<ToolExecutionResult> ExecuteMutationAsync(
        ToolDispatchContext context, IReadOnlyDictionary<string, JsonElement> input,
        string idempotencyKey, CancellationToken cancellationToken)
    {
        MutationCallCount++;
        LastIdempotencyKey = idempotencyKey;
        return Task.FromResult(ToolExecutionResult.Success(MutationResultContent));
    }
}

/// <summary>
/// A subclass that `new`-hides ExecuteAsync in an attempt to bypass
/// authorization. Exists only to prove WriteToolBaseTests's structural
/// guarantee — see ExecuteAsync_CalledThroughInterfaceReference_CannotBeBypassedByNewHiding.
/// </summary>
public class HidingWriteTool : FakeWriteTool
{
    public bool HiddenExecuteAsyncWasCalled { get; private set; }

    public new Task<ToolExecutionResult> ExecuteAsync(
        ToolDispatchContext context, IReadOnlyDictionary<string, JsonElement> input,
        string idempotencyKey, CancellationToken cancellationToken)
    {
        HiddenExecuteAsyncWasCalled = true;
        return Task.FromResult(ToolExecutionResult.Success("bypassed"));
    }
}

/// <summary>
/// Pure unit tests — no DB, no HTTP. Proves Decision 18 (sealed/template-
/// method structural authorization enforcement) directly against the actual
/// WriteToolBase implementation, not just by reading the source.
/// </summary>
public class WriteToolBaseTests
{
    private static readonly ToolDispatchContext Context = new(Guid.NewGuid(), Guid.NewGuid(), null, new CustomerReplyCorrelation());
    private static readonly IReadOnlyDictionary<string, JsonElement> EmptyInput = new Dictionary<string, JsonElement>();

    [Fact]
    public async Task ExecuteAsync_AuthorizationDenied_NeverReachesMutation()
    {
        var tool = new FakeWriteTool { AuthorizationResultToReturn = false, DenialMessage = "not your order" };

        var result = await tool.ExecuteAsync(Context, EmptyInput, "key-1", CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("not your order", result.Content);
        Assert.Equal(1, tool.AuthorizeCallCount);
        Assert.Equal(0, tool.MutationCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_AuthorizationGranted_ReachesMutation_WithSuppliedIdempotencyKey()
    {
        var tool = new FakeWriteTool { AuthorizationResultToReturn = true };

        var result = await tool.ExecuteAsync(Context, EmptyInput, "key-1", CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(1, tool.AuthorizeCallCount);
        Assert.Equal(1, tool.MutationCallCount);
        Assert.Equal("key-1", tool.LastIdempotencyKey);
    }

    [Fact]
    public async Task ExecuteAsync_CalledThroughInterfaceReference_CannotBeBypassedByNewHiding()
    {
        var tool = new HidingWriteTool { AuthorizationResultToReturn = false };
        IWriteDispatchTool asInterface = tool;

        var result = await asInterface.ExecuteAsync(Context, EmptyInput, "key-1", CancellationToken.None);

        // The hidden `new` method never runs when called through the
        // interface — the base class's sealed authorization-then-mutation
        // path is what actually executes, exactly as ToolDispatchService
        // (which only ever holds an IWriteDispatchTool-typed reference)
        // would observe. This is the concrete, executable proof behind
        // WriteToolBase's own doc comment claim.
        Assert.False(tool.HiddenExecuteAsyncWasCalled);
        Assert.True(result.IsError);
        Assert.Equal(1, tool.AuthorizeCallCount);
        Assert.Equal(0, tool.MutationCallCount);
    }
}
