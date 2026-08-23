using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Anthropic.Services;
using backend.Data;
using backend.Enums;
using backend.Models;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

public interface IClaudeReplyService
{
    /// <summary>
    /// Generates a reply from the conversation's full message history and
    /// persists it as Message{Role=Ai}, updating Conversation.LastMessageAt
    /// in the same write. Returns the reply text, or null if the Claude
    /// call failed — the customer's own message (already committed by the
    /// time this is called) is never affected either way.
    /// </summary>
    Task<string?> GenerateReplyAsync(Guid businessId, Guid conversationId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Step 8: conversation reply. Step 12 added RAG grounding — retrieves the
/// business's most relevant active KnowledgeChunks for the latest customer
/// message and, when any are found, appends them to the system prompt and
/// records a MessageKnowledgeSource row per chunk actually injected. Still
/// no tools/dispatch (Step 20, "not built"). Model is claude-sonnet-5 per
/// NIVORA_DEVELOPMENT_PLAN_V2.md's own recorded stack decision, not a
/// default picked here.
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
    ILogger<ClaudeReplyService> logger) : IClaudeReplyService
{
    private const int MaxReplyTokens = 1024;

    public async Task<string?> GenerateReplyAsync(Guid businessId, Guid conversationId, CancellationToken cancellationToken = default)
    {
        var business = await db.Businesses
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(b => b.Id == businessId, cancellationToken);
        if (business is null)
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
        // taking perspective — no Agent-authored messages can exist yet
        // anyway, since no staff-reply UI is built, but the mapping is
        // correct either way).
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

            if (string.IsNullOrWhiteSpace(replyText))
            {
                return null;
            }
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

        var conversation = await db.Conversations
            .IgnoreQueryFilters()
            .FirstAsync(c => c.Id == conversationId, cancellationToken);
        conversation.LastMessageAt = now;
        conversation.UpdatedAt = now;

        await db.SaveChangesAsync(cancellationToken);

        return replyText;
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
