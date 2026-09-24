namespace backend.Constants;

/// <summary>
/// String constants for AiProviderInvocation.Operation — business purpose
/// only, never a class, endpoint, or method name (A1 design
/// docs/handoffs/billing-usage-a1-invocation-capture-design-v11.md §3,
/// Decision 6). Independently verified against all 8 live Anthropic/Voyage
/// call sites in the codebase: every call site maps to exactly one of these
/// six values, none left unused. CustomerReply covers both
/// ClaudeReplyService's no-tool path and ToolDispatchService's tool-loop
/// iterations; KnowledgeIndexing covers both the create and update indexing
/// paths — each pair is distinguished by CorrelationId/Sequence, not by a
/// separate Operation value.
/// </summary>
public static class AiOperations
{
    public const string CustomerReply = "CustomerReply";
    public const string ConversationClassification = "ConversationClassification";
    public const string AnalystSelection = "AnalystSelection";
    public const string AnalystSynthesis = "AnalystSynthesis";
    public const string KnowledgeIndexing = "KnowledgeIndexing";
    public const string KnowledgeRetrievalQuery = "KnowledgeRetrievalQuery";
}
