using backend.Interfaces;

namespace backend.Models;

public class CustomerExtendedLoggingHistory : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }
    public Guid CustomerId { get; set; }

    public Guid EnabledByUserId { get; set; }
    public DateTimeOffset EnabledAt { get; set; }

    public Guid? DisabledByUserId { get; set; }
    public DateTimeOffset? DisabledAt { get; set; }
}