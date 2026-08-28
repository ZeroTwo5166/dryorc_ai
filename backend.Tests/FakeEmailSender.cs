using backend.Email;

namespace backend.Tests;

/// <summary>
/// Registered in place of the real Resend-backed IEmailSender for normal
/// (non-live) test execution — see NivoraApiFactory. Step 21's OTP request
/// endpoint is the first widget-reachable path to actually send an email;
/// without this, every test hitting it would fire a real, billed Resend
/// call, the same reasoning FakeMessageService/FakeVoyageEmbeddingClient
/// already established for their own external APIs.
/// </summary>
public class FakeEmailSender : IEmailSender
{
    public List<(string To, string Subject, string HtmlBody)> SentEmails { get; } = [];

    public Task SendEmailAsync(string to, string subject, string htmlBody)
    {
        SentEmails.Add((to, subject, htmlBody));
        return Task.CompletedTask;
    }
}
