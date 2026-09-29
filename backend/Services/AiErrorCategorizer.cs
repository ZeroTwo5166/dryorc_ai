using System.Net;
using Anthropic.Exceptions;
using backend.Constants;

namespace backend.Services;

/// <summary>
/// Maps a provider-call exception to one of the fixed
/// <see cref="AiErrorCategories"/> values (A1 design v11 §3). Reads only
/// exception types, structured status codes and the caller's token — never
/// an exception's message or response body, so raw provider text can never
/// reach <c>ErrorCategory</c> (v11 §8 Test 6).
///
/// OD-3 is locked by owner ruling (the decisions addendum §10 still lists it
/// as open; a docs-only errata records the ruling). The mapping is the Slice
/// 3 design pass's candidate table (§5 OD-3):
/// <list type="bullet">
/// <item>Anthropic: 429 → <see cref="AiErrorCategories.RateLimited"/>;
/// 5xx (incl. 529) → <see cref="AiErrorCategories.ServerError"/>; other 4xx
/// (e.g. 400, 404) → <see cref="AiErrorCategories.InvalidRequest"/>;
/// <c>AnthropicIOException</c> (network) → <see cref="AiErrorCategories.Unknown"/>;
/// cancellation with the caller's token cancelled → <see cref="AiErrorCategories.Cancelled"/>,
/// any other cancellation (SDK/HttpClient timeout) → <see cref="AiErrorCategories.Timeout"/>;
/// anything else → <see cref="AiErrorCategories.Unknown"/>.</item>
/// <item>Voyage (status transport option (c)): <c>VoyageEmbeddingException</c>
/// carries no structured status, so only <see cref="AiErrorCategories.Cancelled"/>,
/// <see cref="AiErrorCategories.Timeout"/> and <see cref="AiErrorCategories.Unknown"/>
/// are produced. HTTP 429/5xx land as Unknown — a known deferred gap.</item>
/// </list>
/// </summary>
public static class AiErrorCategorizer
{
    public static string ForAnthropic(Exception exception, CancellationToken callerToken)
    {
        if (exception is OperationCanceledException)
        {
            return callerToken.IsCancellationRequested ? AiErrorCategories.Cancelled : AiErrorCategories.Timeout;
        }

        return exception switch
        {
            AnthropicRateLimitException => AiErrorCategories.RateLimited,
            Anthropic5xxException => AiErrorCategories.ServerError,
            AnthropicApiException api => FromStatus(api.StatusCode),
            // Network failures: SDK 12.42.0 wraps HttpRequestException in
            // AnthropicIOException (Slice 3 design pass §2.1, V-A1).
            AnthropicIOException => AiErrorCategories.Unknown,
            _ => AiErrorCategories.Unknown,
        };
    }

    public static string ForVoyage(Exception exception, CancellationToken callerToken)
    {
        if (exception is OperationCanceledException && callerToken.IsCancellationRequested)
        {
            return AiErrorCategories.Cancelled;
        }

        // VoyageEmbeddingClient wraps an HttpClient timeout (a
        // TaskCanceledException while the caller's token is not cancelled)
        // as VoyageEmbeddingException with that inner exception.
        if (exception is OperationCanceledException
            || exception is VoyageEmbeddingException { InnerException: TaskCanceledException })
        {
            return AiErrorCategories.Timeout;
        }

        return AiErrorCategories.Unknown;
    }

    private static string FromStatus(HttpStatusCode status) => (int)status switch
    {
        429 => AiErrorCategories.RateLimited,
        >= 500 and <= 599 => AiErrorCategories.ServerError,
        >= 400 and <= 499 => AiErrorCategories.InvalidRequest,
        _ => AiErrorCategories.Unknown,
    };
}
