using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace backend.Email;

/// <summary>
/// Raw HTTP client for Resend's send API (https://resend.com/docs/api-reference/emails/send-email).
/// No SDK package — same "hand-rolled HTTP client" precedent as VoyageEmbeddingService,
/// since a full SDK is unnecessary for one endpoint. The HttpClient's
/// BaseAddress/Authorization header are configured once in Program.cs.
/// </summary>
public class ResendEmailSender(
    HttpClient httpClient,
    IOptions<ResendOptions> options,
    ILogger<ResendEmailSender> logger) : IEmailSender
{
    public async Task SendEmailAsync(string to, string subject, string htmlBody)
    {
        var payload = new
        {
            from = options.Value.FromEmail,
            to = new[] { to },
            subject,
            html = htmlBody,
        };

        using var response = await httpClient.PostAsJsonAsync("emails", payload);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            logger.LogError(
                "Resend email send to {Recipient} failed with {Status}: {Body}",
                to, (int)response.StatusCode, body);
            throw new InvalidOperationException(
                $"Failed to send email via Resend ({(int)response.StatusCode}).");
        }
    }
}
