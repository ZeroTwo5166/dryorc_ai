using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Anthropic.Services;
using backend.Data;
using backend.Enums;
using backend.Models;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

/// <summary>
/// The persisted AI message's own stable identity — distinct from the
/// customer message that triggered it. Customer-side delivery (widget
/// polling) needs the AI message's real Id/CreatedAt to dedup correctly
/// against the same message arriving later via GET /api/widget/messages;
/// returning only the reply text here (as before) left no way for a caller
/// to know that identity.
/// </summary>
public record AiReplyResult(Guid Id, string Content, DateTimeOffset CreatedAt);

public interface IClaudeReplyService
{
    /// <summary>
    /// Generates a reply from the conversation's full message history and
    /// persists it as Message{Role=Ai}, updating Conversation.LastMessageAt
    /// in the same write. Returns the persisted message's id/content/
    /// timestamp, or null if the Claude call failed, or if the conversation
    /// is HandledBy.Human (Step 9: a staff member has already replied — see
    /// Conversation.HandledBy's own doc comment) — the customer's own
    /// message (already committed by the time this is called) is never
    /// affected either way. Throws ConversationBusinessMismatchException,
    /// after writing the rule (c) audit row and before any provider call,
    /// when the conversation does not belong to businessId (v11 §5).
    /// </summary>
    Task<AiReplyResult?> GenerateReplyAsync(Guid businessId, Guid conversationId, CancellationToken cancellationToken = default);
}

/// <summary>
/// v11 §5 rule (c) step 3. Carries no conversation or business id: the
/// audit row (written first) is the record; this only blocks the turn.
/// </summary>
public sealed class ConversationBusinessMismatchException()
    : Exception("The conversation does not belong to the authenticated business; the AI reply was blocked.");

/// <summary>
/// Step 8: conversation reply. Step 12 added RAG grounding — retrieves the
/// business's most relevant active KnowledgeChunks for the latest customer
/// message and, when any are found, appends them to the system prompt and
/// records a MessageKnowledgeSource row per chunk actually injected. Still
/// no tools/dispatch (Step 20, "not built"). Model is claude-sonnet-5 per
/// NIVORA_DEVELOPMENT_PLAN_V2.md's own recorded stack decision, not a
/// default picked here.
///
/// Step 9 added a narrow gate: a conversation with HandledBy == Human (set
/// by the staff-reply endpoint on its first Agent message) never gets an
/// automatic reply here — see Conversation.HandledBy's own doc comment.
/// This is the full extent of Step 9's interaction with this service; no
/// other behavior changed.
///
/// Deliberately called AFTER the customer message's own transaction has
/// already committed (see WidgetController.PostMessage) — same reasoning
/// NIVORA_SCHEMA_AND_LOGIC_REFERENCE.md Section 3.4 already states for
/// Shopify calls: an external HTTP call must never hold a DB
/// transaction/lock open. A Claude failure here degrades to "no AI reply
/// this turn," never an error surfaced to the widget — the customer's
/// message is safe regardless. Retrieval failure degrades the same way
/// (see the try/catch around retrievalService.RetrieveAsync below) — RAG is
/// an optional enhancement to this conversation, never something that can
/// break it, unlike Step 11's write-path indexing which fails atomically.
/// </summary>
public class ClaudeReplyService(
    AppDbContext db,
    IMessageService messageService,
    IKnowledgeRetrievalService retrievalService,
    IToolDispatchService toolDispatchService,
    IConversationClassificationService classificationService,
    IUnauthorizedActionAuditWriter auditWriter,
    ILogger<ClaudeReplyService> logger) : IClaudeReplyService
{
    private const int MaxReplyTokens = 1024;

    public async Task<AiReplyResult?> GenerateReplyAsync(Guid businessId, Guid conversationId, CancellationToken cancellationToken = default)
    {
        var business = await db.Businesses
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(b => b.Id == businessId, cancellationToken);
        if (business is null)
        {
            return null;
        }

        var conversation = await db.Conversations
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken);
        if (conversation is null)
        {
            return null;
        }

        // v11 §5 rule (b)/(c): checked once, here, before any provider-call context
        // exists; A1/A2/A3/V3 inherit it (v11 §5:178).
        if (conversation.BusinessId != businessId)
        {
            await auditWriter.WriteConversationBusinessMismatchBlockedAsync(businessId);
            throw new ConversationBusinessMismatchException();
        }

        // Step 9: once a staff member has replied, HandledBy flips to Human
        // and this conversation stops receiving automatic Claude replies —
        // a human is now answering. See Conversation.HandledBy's own doc
        // comment. Checked before any Claude/retrieval call is made, not
        // just before persisting, so a Human-handled conversation never
        // incurs the Voyage/Anthropic calls at all.
        if (conversation.HandledBy == HandledBy.Human)
        {
            return null;
        }

        var history = await db.Messages
            .IgnoreQueryFilters()
            .Where(m => m.BusinessId == businessId && m.ConversationId == conversationId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(cancellationToken);

        if (history.Count == 0)
        {
            return null;
        }

        // Customer -> User; both Ai and Agent map to Assistant (both are
        // "the business's side" of the conversation from Claude's turn-
        // taking perspective). Agent-authored messages can exist since Step
        // 9, but by the time any exist on a conversation, HandledBy is
        // already Human and the guard above already returned — so an Agent
        // message is never actually seen by the history-mapping below in
        // practice; the mapping is kept correct regardless.
        var messages = history
            .Select(m => new MessageParam
            {
                Role = m.Role == MessageRole.Customer ? Role.User : Role.Assistant,
                Content = m.Content,
            })
            .ToList();

        // Retrieved for every customer message, deliberately — no
        // classification/heuristic on whether this particular message
        // "needs" knowledge grounding. Simpler, predictable, and the extra
        // Voyage call per turn is an accepted MVP cost. Query text is the
        // latest customer-authored message in this conversation (the one
        // that triggered this reply), not the full history.
        var queryText = history.LastOrDefault(m => m.Role == MessageRole.Customer)?.Content;
        IReadOnlyList<RetrievedKnowledgeChunk> retrievedChunks = [];
        if (queryText is not null)
        {
            try
            {
                retrievedChunks = await retrievalService.RetrieveAsync(businessId, queryText, cancellationToken);
            }
            catch (Exception ex)
            {
                // Broad catch, deliberately — unlike the narrow, typed catch
                // chain below for Claude itself (where a genuine
                // programming/config error is allowed to propagate loudly),
                // retrieval must never be capable of breaking a widget
                // conversation for ANY reason. KnowledgeRetrievalService's
                // own implementation already degrades gracefully for every
                // expected Voyage failure; this is defense-in-depth against
                // an unexpected one (a real bug here would otherwise be
                // silently and permanently misreported as "no relevant
                // knowledge found" with no way to detect it), not the
                // primary mechanism.
                //
                // Logs the exception TYPE only, never ex.Message/ex.ToString()
                // — VoyageEmbeddingException can carry a raw Voyage API error
                // response body in its own message (see
                // VoyageEmbeddingClient's non-success-status branch), which
                // must never end up in logs. No query text or chunk content
                // is logged either, only safe identifiers.
                logger.LogWarning(
                    "Knowledge retrieval failed with {ExceptionType} for conversation {ConversationId}; falling back to conversation-only reply.",
                    ex.GetType().Name, conversationId);
                retrievedChunks = [];
            }
        }

        string replyText;

        // Step 23K: tools are offered only when a customer is actually
        // resolved on this conversation — ToolDispatchContext.CustomerId is
        // non-nullable (IDispatchTool.cs), so a conversation with no
        // resolved customer cannot construct one at all. This does not
        // resolve the broader open question 23H/23I flagged (how
        // IActionProposalTool's own proposal-time customer-null refusal,
        // 22Z §8, should interact with this same nullability) — it only
        // avoids constructing an invalid context here, for this one
        // read-only tool. When no customer is resolved, behavior is
        // completely unchanged from before this pass: the original,
        // direct, tool-free Claude call below still runs exactly as it did.
        if (conversation.CustomerId is Guid customerId)
        {
            var dispatchContext = new ToolDispatchContext(businessId, customerId, conversationId);

            // Only added in this branch — the base prompt's own blanket "no
            // actions" disclaimer was written for a zero-tool world and
            // doesn't mention read-only lookups at all; without this, a
            // model reading it has no reason to expect a tool exists to
            // call. Not added to the no-tools branch below, which
            // genuinely has nothing to offer.
            var toolAwareSystemPrompt = BuildSystemPrompt(business.Name, retrievedChunks) +
                "\n\nYou have a getCustomer tool that looks up the customer's name and email on file. " +
                "Use it whenever the customer asks about their own account/profile details.";

            // Step 48: a narrow, single-tool kill switch — Business.CancelOrderToolEnabled
            // is checked here (the Business row is already loaded above, no
            // extra query) rather than inside ToolDispatchService, which
            // deliberately stays free of a database dependency for its own
            // pure-unit-test suite. Default false — an opted-out business's
            // AI is never told this tool exists at all, not offered and
            // then refused. Explicitly not Decision 8's deferred
            // per-write-tool automation-tier system — see
            // docs/handoffs/step-48-cancelorder-tool-registration-production-safety-scoping.md.
            //
            // Step refundOrder Phase 3 — Owner Decision 3, Layer 1 (design
            // §14.3): Business.RefundToolEnabled gates refundOrder's own
            // exposure identically. This is a second, independent per-tool
            // flag, not a reuse of CancelOrderToolEnabled — each tool is
            // disabled/enabled on its own. Layer 2 (execution-time
            // re-check) lives in RefundApprovedActionExecutor, not here.
            var disabledToolNames = new HashSet<string>();
            if (!business.CancelOrderToolEnabled)
            {
                disabledToolNames.Add(OrderCancelProposalTool.ToolName);
            }
            if (!business.RefundToolEnabled)
            {
                disabledToolNames.Add(RefundProposalTool.ToolName);
            }

            // Step getOrder — Business.ShopifyReadToolsEnabled gates getOrder's
            // own exposure identically. A third, independent per-business
            // flag scoped to the read-only tool surface, not a reuse of
            // CancelOrderToolEnabled/RefundToolEnabled (those gate mutation
            // permission, this gates read permission — conflating them would
            // let a business that never enabled cancelOrder still implicitly
            // grant broad Shopify-read access, and vice versa). See
            // docs/handoffs/step-getorder-implementation-design.md §1.9/§12.
            if (!business.ShopifyReadToolsEnabled)
            {
                disabledToolNames.Add(GetOrderTool.ToolName);
                disabledToolNames.Add(GetProductTool.ToolName);
                disabledToolNames.Add(SearchProductsTool.ToolName);
                disabledToolNames.Add(GetInventoryTool.ToolName);
            }

            var outcome = await toolDispatchService.RunAsync(
                dispatchContext, toolAwareSystemPrompt, messages, disabledToolNames, cancellationToken);

            // ToolDispatchService.RunAsync already internally catches the
            // same Anthropic exception types the inline path below handles,
            // converting them to ClaudeCallFailed — no duplicate catch chain
            // needed here. AnthropicNotConfiguredException still propagates
            // uncaught either way (RunAsync's own doc comment states the
            // identical policy this method's own removed catch chain used
            // to state directly).
            if (outcome.Status != ToolDispatchStatus.Completed)
            {
                return null;
            }

            replyText = outcome.ResponseText!;
        }
        else
        {
            try
            {
                var response = await messageService.Create(new MessageCreateParams
                {
                    Model = "claude-sonnet-5",
                    MaxTokens = MaxReplyTokens,
                    System = BuildSystemPrompt(business.Name, retrievedChunks),
                    Messages = messages,
                }, cancellationToken);

                replyText = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
            }
            // Most-specific-first, matching the SDK's own hierarchy
            // (AnthropicRateLimitException : Anthropic4xxException : ...
            // : AnthropicApiException : AnthropicServiceException) — the
            // AnthropicApiException catch below would already catch the two
            // above it too, kept separate only for clarity/intent, not because
            // it changes behavior (all three do the same thing here).
            catch (AnthropicRateLimitException)
            {
                return null;
            }
            catch (Anthropic5xxException)
            {
                return null;
            }
            catch (AnthropicApiException)
            {
                return null;
            }
            // Genuine network-level failures reaching Anthropic at all
            // (DNS/connection refused/etc.) or a request timeout — distinct
            // from the SDK's own typed API-error hierarchy above, but still an
            // external failure, not a configuration/programming error.
            catch (HttpRequestException)
            {
                return null;
            }
            catch (TaskCanceledException)
            {
                return null;
            }
            // Deliberately NOT a bare `catch (Exception)` anymore — an
            // AnthropicNotConfiguredException (thrown by
            // MissingAnthropicApiKeyMessageService when Anthropic:ApiKey isn't
            // configured) must propagate out of this method uncaught. It's a
            // programming/deployment error, not "Claude declined to reply
            // this turn" — silently folding it into a null return here would
            // make a missing API key indistinguishable from a normal, expected
            // external failure, which is exactly what must not happen.
        }

        if (string.IsNullOrWhiteSpace(replyText))
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var aiMessage = new backend.Models.Message // Anthropic.Models.Messages also defines a Message type — qualify to disambiguate
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            ConversationId = conversationId,
            Role = MessageRole.Ai,
            Content = replyText,
            CreatedAt = now,
        };
        db.Messages.Add(aiMessage);

        // One row per chunk actually injected into the prompt above — every
        // element of retrievedChunks, not a subset. These rows mean "this
        // source was retrieved and supplied to Claude for this response,"
        // never "Claude definitely used/cited it" — there is no tool-calling
        // or output parsing here that could support that stronger claim.
        // aiMessage.Id is already assigned (client-generated) before
        // SaveChangesAsync runs below, so it's usable here even though the
        // row hasn't been persisted yet — both inserts commit together in
        // the one SaveChangesAsync call, never as two separate writes.
        if (retrievedChunks.Count > 0)
        {
            db.MessageKnowledgeSources.AddRange(retrievedChunks.Select(c => new MessageKnowledgeSource
            {
                Id = Guid.NewGuid(),
                BusinessId = businessId,
                MessageId = aiMessage.Id,
                KnowledgeDocumentId = c.KnowledgeDocumentId,
                KnowledgeChunkId = c.KnowledgeChunkId,
                DocumentTitleSnapshot = c.DocumentTitle,
                ChunkContentSnapshot = c.Content,
                CreatedAt = now,
            }));
        }

        conversation.LastMessageAt = now;
        conversation.UpdatedAt = now;

        await db.SaveChangesAsync(cancellationToken);

        // Step 22 §A — exactly one classification attempt per successful
        // reply, regardless of which branch above produced replyText: this
        // is the single point both branches converge on (each sets
        // replyText and falls through to this shared tail; neither branch
        // calls classification itself). Deliberately AFTER the reply is
        // already committed, using the same messages list that produced
        // it — mirrors WidgetConversationService.NotifyStaffOfCustomerReplyAsync's
        // own documented reasoning for its broad try/catch(Exception): a
        // secondary, already-optional side effect must never be able to
        // fail an already-succeeded, already-persisted primary result.
        // ConversationClassificationService.ClassifyAsync already narrowly
        // catches every expected Claude/HTTP failure internally (logging
        // its own outcome) — this is defense-in-depth against a genuinely
        // unexpected exception only.
        try
        {
            await classificationService.ClassifyAsync(businessId, conversationId, messages, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "Unexpected failure running conversation classification for {ConversationId}; reply already succeeded.",
                conversationId);
        }

        return new AiReplyResult(aiMessage.Id, replyText, now);
    }

    private static string BuildSystemPrompt(string businessName, IReadOnlyList<RetrievedKnowledgeChunk> retrievedChunks)
    {
        var basePrompt = BuildBaseSystemPrompt(businessName);
        if (retrievedChunks.Count == 0)
        {
            return basePrompt;
        }

        var contextSection = string.Join("\n", retrievedChunks.Select((c, i) => $"[{i + 1}] {c.Content}"));
        return $"""
            {basePrompt}

            Knowledge base context (use if relevant; if the answer isn't covered here, say so plainly rather than guessing):
            {contextSection}
            """;
    }

    private static string BuildBaseSystemPrompt(string businessName) =>
        $"""
        You are an AI customer support assistant for {businessName}. Answer the customer's questions helpfully and honestly.

        If you don't know something, say so plainly rather than guessing. If the customer asks whether you're an AI, or whether they're talking to a real person, say plainly that you're an AI assistant.

        You cannot perform any actions on the customer's behalf — no refunds, cancellations, order changes, or account updates. You can only discuss and explain; never claim to have done something you haven't.
        """;
}
