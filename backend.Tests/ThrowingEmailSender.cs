using backend.Email;

namespace backend.Tests;

/// <summary>
/// Step 21 Finding A remediation tests. Always throws from SendEmailAsync,
/// signaling once invoked — used to prove a detached dispatch failure is
/// caught and logged rather than becoming an unobserved task exception.
/// </summary>
public class ThrowingEmailSender : IEmailSender
{
    private readonly TaskCompletionSource _called = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Called => _called.Task;

    public Task SendEmailAsync(string to, string subject, string htmlBody)
    {
        _called.TrySetResult();
        throw new InvalidOperationException("Simulated delivery failure for test.");
    }
}
