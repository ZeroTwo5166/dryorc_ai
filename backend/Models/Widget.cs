using backend.Interfaces;

namespace backend.Models;

public class Widget : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }

    public required string Name { get; set; }

    /// <summary>
    /// The identifier embedded in the public-facing widget script
    /// (&lt;script src="...?key=..."&gt;). Deliberately separate from Id
    /// so it can be rotated (e.g. if scraped/leaked) without breaking the
    /// Conversation.WidgetId FK chain to past conversations. Not a secret
    /// on its own — anyone viewing the business's page source can see it.
    /// Server-side session/token validation on every request is the real
    /// authorization boundary, not this key.
    /// </summary>
    public required string PublicKey { get; set; }

    /// <summary>
    /// Domains this widget is permitted to run on, e.g. "nike.com". Checked
    /// against the request's Origin header. A useful filter against
    /// accidental misuse and casual scraping — not a real security
    /// boundary, since Origin can be omitted/forged by non-browser clients.
    /// </summary>
    public List<string> AllowedDomains { get; set; } = [];

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

