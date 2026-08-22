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
/// Step 8: conversation-only reply, no tools, no RAG/knowledge-base
/// grounding (both explicitly later steps per NIVORA_FULL_PLAN_CHECKLIST_V2.md
/// — GetReplyWithToolsAsync/Step 20 is "not built"). Model is
/// claude-sonnet-5 per NIVORA_DEVELOPMENT_PLAN_V2.md's own recorded stack
/// decision, not a default picked here.
///
/// Deliberately called AFTER the customer message's own transaction has
/// already committed (see WidgetController.PostMessage) — same reasoning
/// NIVORA_SCHEMA_AND_LOGIC_REFERENCE.md Section 3.4 already states for
/// Shopify calls: an external HTTP call must never hold a DB
/// transaction/lock open. A Claude failure here degrades to "no AI reply
/// this turn," never an error surfaced to the widget — the customer's
/// message is safe regardless.
/// </summary>
public class ClaudeReplyService(AppDbContext db, IMessageService messageService) : IClaudeReplyService
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

        string replyText;
        try
        {
            var response = await messageService.Create(new MessageCreateParams
            {
                Model = "claude-sonnet-5",
                MaxTokens = MaxReplyTokens,
                System = BuildSystemPrompt(business.Name),
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
        db.Messages.Add(new backend.Models.Message // Anthropic.Models.Messages also defines a Message type — qualify to disambiguate
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            ConversationId = conversationId,
            Role = MessageRole.Ai,
            Content = replyText,
            CreatedAt = now,
        });

        var conversation = await db.Conversations
            .IgnoreQueryFilters()
            .FirstAsync(c => c.Id == conversationId, cancellationToken);
        conversation.LastMessageAt = now;
        conversation.UpdatedAt = now;

        await db.SaveChangesAsync(cancellationToken);

        return replyText;
    }

    private static string BuildSystemPrompt(string businessName) =>
        $"""
        You are an AI customer support assistant for {businessName}. Answer the customer's questions helpfully and honestly.

        If you don't know something, say so plainly rather than guessing. If the customer asks whether you're an AI, or whether they're talking to a real person, say plainly that you're an AI assistant.

        You cannot perform any actions on the customer's behalf — no refunds, cancellations, order changes, or account updates. You can only discuss and explain; never claim to have done something you haven't.
        """;
}
