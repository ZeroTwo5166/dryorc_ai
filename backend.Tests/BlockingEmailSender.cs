using backend.Email;

namespace backend.Tests;

/// <summary>
/// Step 21 Finding A remediation tests. Blocks SendEmailAsync until the
/// test explicitly releases it — used to deterministically prove a caller
/// does NOT await email delivery (the caller must be able to complete while
/// this sender is still blocked mid-send), without any arbitrary sleep.
/// </summary>
public class BlockingEmailSender : IEmailSender
{
    private readonly TaskCompletionSource _sendStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<(string To, string Subject, string HtmlBody)> SentEmails { get; } = [];

    /// <summary>Completes once SendEmailAsync has actually been invoked — not once it has finished.</summary>
    public Task SendStarted => _sendStarted.Task;

    public void Release() => _releaseGate.TrySetResult();

    public async Task SendEmailAsync(string to, string subject, string htmlBody)
    {
        _sendStarted.TrySetResult();
        await _releaseGate.Task;
        SentEmails.Add((to, subject, htmlBody));
    }
}
