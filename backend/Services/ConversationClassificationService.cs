using System.Diagnostics;
using System.Text.Json;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using backend.Constants;
using backend.Data;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

/// <summary>
/// Step 22 — outcome of one classification attempt, logged unconditionally
/// per §A's structured-logging requirement. AllTagsInvalid is distinct from
/// UnparseableResult (design doc §B, "Validation Outcomes"): the former
/// means the model returned a structurally valid response but every named
/// value failed the allowed-set membership check; the latter means no
/// usable structured response was produced at all. Success also covers the
/// deliberate {primaryTag: null, secondaryTags: []} "no tags apply" case —
/// a normal, valid classification, never AllTagsInvalid.
/// </summary>
internal enum ClassificationOutcome
{
    Success,
    AllTagsInvalid,
    UnparseableResult,
    ClaudeCallFailed,
}

public interface IConversationClassificationService
{
    /// <summary>
    /// Runs exactly one classification attempt for one customer-reply turn.
    /// Never throws for any expected external-failure condition (Claude
    /// rate limit/5xx/timeout, an unparseable or all-invalid response) —
    /// each degrades to a logged outcome and, except for Success, no write.
    /// Callers must still guard against a genuinely unexpected exception
    /// per their own call-site policy (see ClaudeReplyService's own call
    /// site for why).
    /// </summary>
    Task ClassifyAsync(
        Guid businessId, Guid conversationId, IReadOnlyList<MessageParam> messages,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Step 22 §A — a forced single-tool call (ToolChoiceTool), synchronously
/// awaited by ClaudeReplyService immediately after the customer-facing
/// reply path completes. Deliberately NOT routed through
/// ToolDispatchService/IDispatchTool: that family's tools assume a static,
/// business-invariant InputSchema, but this call's InputSchema must be
/// built fresh per business (§B) — a real architectural difference from
/// every existing dispatch tool, not an oversight.
/// </summary>
public class ConversationClassificationService(
    AppDbContext db,
    IAnthropicInvoker anthropicInvoker,
    IConversationModeService modeService,
    ILogger<ConversationClassificationService> logger) : IConversationClassificationService
{
    private const string ToolName = "classifyConversation";
    private const int MaxReplyTokens = 256;

    public async Task ClassifyAsync(
        Guid businessId, Guid conversationId, IReadOnlyList<MessageParam> messages,
        CancellationToken cancellationToken = default)
    {
        var allowedTags = await BuildAllowedTagsAsync(businessId, cancellationToken);

        var stopwatch = Stopwatch.StartNew();
        Message response;
        try
        {
            response = await anthropicInvoker.CreateMessageAsync(new AiCallContext(businessId, AiOperations.ConversationClassification, Guid.NewGuid(), 0, conversationId), new MessageCreateParams
            {
                Model = AnthropicModels.Default,
                MaxTokens = MaxReplyTokens,
                System = BuildSystemPrompt(),
                Messages = messages.ToList(),
                Tools = [(ToolUnion)BuildTool(allowedTags)],
                ToolChoice = new ToolChoiceTool(ToolName),
            }, cancellationToken);
        }
        // Same typed hierarchy and reasoning as ClaudeReplyService/
        // ToolDispatchService's own catch chains — a genuine external/
        // transient failure degrades to a logged outcome, never thrown.
        // AnthropicNotConfiguredException is deliberately NOT caught here,
        // matching both of those call sites' own policy — and is provably
        // unreachable in practice: this method only ever runs after the
        // customer-facing reply's own Claude call (using the same
        // IMessageService/API key) already succeeded, so a missing API key
        // would already have surfaced there, before this method is ever
        // invoked.
        catch (AnthropicRateLimitException)
        {
            LogOutcome(ClassificationOutcome.ClaudeCallFailed, businessId, conversationId, stopwatch, usage: null);
            return;
        }
        catch (Anthropic5xxException)
        {
            LogOutcome(ClassificationOutcome.ClaudeCallFailed, businessId, conversationId, stopwatch, usage: null);
            return;
        }
        catch (AnthropicApiException)
        {
            LogOutcome(ClassificationOutcome.ClaudeCallFailed, businessId, conversationId, stopwatch, usage: null);
            return;
        }
        catch (HttpRequestException)
        {
            LogOutcome(ClassificationOutcome.ClaudeCallFailed, businessId, conversationId, stopwatch, usage: null);
            return;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogOutcome(ClassificationOutcome.ClaudeCallFailed, businessId, conversationId, stopwatch, usage: null);
            return;
        }
        stopwatch.Stop();

        var toolUse = response.Content.Select(b => b.Value).OfType<ToolUseBlock>()
            .FirstOrDefault(t => t.Name == ToolName);
        if (toolUse is null)
        {
            LogOutcome(ClassificationOutcome.UnparseableResult, businessId, conversationId, stopwatch, response.Usage);
            return;
        }

        string? rawPrimaryTag;
        List<string> rawSecondaryTags;
        try
        {
            rawPrimaryTag = toolUse.Input.TryGetValue("primaryTag", out var primaryEl)
                && primaryEl.ValueKind != JsonValueKind.Null
                ? primaryEl.GetString()
                : null;

            rawSecondaryTags = toolUse.Input.TryGetValue("secondaryTags", out var secondaryEl)
                && secondaryEl.ValueKind == JsonValueKind.Array
                ? secondaryEl.EnumerateArray().Select(e => e.GetString()!).Where(s => s is not null).ToList()
                : [];
        }
        catch (InvalidOperationException)
        {
            // toolUse.Input's shape didn't match what GetString()/
            // EnumerateArray() expect — a malformed response, not a
            // validation failure on otherwise-well-shaped values.
            LogOutcome(ClassificationOutcome.UnparseableResult, businessId, conversationId, stopwatch, response.Usage);
            return;
        }

        var primaryProvided = rawPrimaryTag is not null;
        var normalizedPrimary = primaryProvided ? Normalize(rawPrimaryTag!) : null;
        var primaryValid = normalizedPrimary is not null && allowedTags.Contains(normalizedPrimary);

        var normalizedSecondary = rawSecondaryTags.Select(Normalize).Distinct().ToList();
        var secondaryValid = normalizedSecondary.Where(allowedTags.Contains).ToList();

        var anyProvided = primaryProvided || rawSecondaryTags.Count > 0;
        var anySurvived = primaryValid || secondaryValid.Count > 0;

        if (!anyProvided)
        {
            // Deliberate {primaryTag: null, secondaryTags: []} — the model
            // considered the allowed set and chose nothing. A normal,
            // valid, successful response (design doc §B) — replaces
            // whatever Tags/PrimaryTag previously held, never
            // AllTagsInvalid.
            await modeService.ApplyTagsAndRatchetAsync(businessId, conversationId, (conversation, _) =>
            {
                conversation.PrimaryTag = null;
                conversation.Tags = [];
                return Task.CompletedTask;
            }, cancellationToken);
            LogOutcome(ClassificationOutcome.Success, businessId, conversationId, stopwatch, response.Usage);
            return;
        }

        if (!anySurvived)
        {
            // Something was named, but every named value failed membership
            // validation — no write (design doc §B, "Validation Outcomes").
            LogOutcome(ClassificationOutcome.AllTagsInvalid, businessId, conversationId, stopwatch, response.Usage);
            return;
        }

        // Option A (design doc §B addendum): a null/invalid primaryTag
        // alongside valid secondaryTags is an accepted partial result, not
        // a failure — finalPrimary is simply null in that case.
        var finalPrimary = primaryValid ? normalizedPrimary : null;
        var finalTags = (finalPrimary is not null ? [finalPrimary] : Array.Empty<string>())
            .Concat(secondaryValid)
            .Distinct()
            .ToList();

        await modeService.ApplyTagsAndRatchetAsync(businessId, conversationId, (conversation, _) =>
        {
            conversation.PrimaryTag = finalPrimary;
            conversation.Tags = finalTags;
            return Task.CompletedTask;
        }, cancellationToken);
        LogOutcome(ClassificationOutcome.Success, businessId, conversationId, stopwatch, response.Usage);
    }

    /// <summary>
    /// §B — the per-call, tenant-scoped allowed-tag union: NivoraCoreTags
    /// (already lowercase) ∪ this business's own TagModeMapping.Tag values,
    /// each normalized via .Trim().ToLowerInvariant() before inclusion —
    /// the same transform ConversationModeService's own matching already
    /// applies at read time (ConversationModeService.cs:61/:84), reused
    /// here rather than introducing a second normalization convention.
    /// Deliberately queried fresh per call, per business — never a
    /// shared/static schema, unlike every existing IDispatchTool.InputSchema.
    /// internal, not private: BuildAllowedTagsAsync is independently
    /// tested (cross-tenant exclusion) without needing a real/fake Claude
    /// call.
    /// </summary>
    internal async Task<HashSet<string>> BuildAllowedTagsAsync(Guid businessId, CancellationToken cancellationToken)
    {
        var customTags = await db.TagModeMappings
            .IgnoreQueryFilters()
            .Where(tm => tm.BusinessId == businessId)
            .Select(tm => tm.Tag)
            .ToListAsync(cancellationToken);

        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tag in NivoraCoreTags.All)
        {
            allowed.Add(Normalize(tag));
        }
        foreach (var tag in customTags)
        {
            allowed.Add(Normalize(tag));
        }

        return allowed;
    }

    private static string Normalize(string tag) => tag.Trim().ToLowerInvariant();

    private static Tool BuildTool(IReadOnlyCollection<string> allowedTags)
    {
        var enumValues = allowedTags.ToArray();

        return new Tool
        {
            Name = ToolName,
            Description = "Classify this conversation with at most one primary tag and any number of " +
                "secondary tags, chosen only from the provided allowed values. Leave primaryTag null and " +
                "secondaryTags empty if no tag clearly applies yet — do not guess.",
            InputSchema = new InputSchema
            {
                Type = JsonSerializer.SerializeToElement("object"),
                Properties = new Dictionary<string, JsonElement>
                {
                    ["primaryTag"] = JsonSerializer.SerializeToElement(new
                    {
                        type = new[] { "string", "null" },
                        @enum = enumValues,
                        description = "The single most specific classification for this conversation, " +
                            "or null if none clearly applies yet.",
                    }),
                    ["secondaryTags"] = JsonSerializer.SerializeToElement(new
                    {
                        type = "array",
                        items = new { type = "string", @enum = enumValues },
                        description = "Any additional, less-central classifications that also apply. May be empty.",
                    }),
                },
                Required = ["primaryTag", "secondaryTags"],
            },
        };
    }

    private static string BuildSystemPrompt() =>
        "You classify customer support conversations for internal routing. " +
        "Choose tags only from the allowed values provided in the tool's own schema — never invent a new tag.";

    private void LogOutcome(
        ClassificationOutcome outcome, Guid businessId, Guid conversationId, Stopwatch stopwatch, Usage? usage)
    {
        if (outcome == ClassificationOutcome.Success)
        {
            logger.LogInformation(
                "Conversation classification for {ConversationId} (business {BusinessId}): {Outcome} in " +
                "{ElapsedMs}ms (input={InputTokens}, output={OutputTokens} tokens).",
                conversationId, businessId, outcome, stopwatch.ElapsedMilliseconds, usage?.InputTokens, usage?.OutputTokens);
        }
        else
        {
            logger.LogWarning(
                "Conversation classification for {ConversationId} (business {BusinessId}): {Outcome} in " +
                "{ElapsedMs}ms (input={InputTokens}, output={OutputTokens} tokens).",
                conversationId, businessId, outcome, stopwatch.ElapsedMilliseconds, usage?.InputTokens, usage?.OutputTokens);
        }
    }
}
