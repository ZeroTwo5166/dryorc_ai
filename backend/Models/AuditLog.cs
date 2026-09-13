using backend.Enums;
using backend.Interfaces;

namespace backend.Models;



public class AuditLog : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }

    /// <summary>See <see cref="AuditEntityTypes"/> for valid values.</summary>
    public required string EntityType { get; set; }

    public required string EntityId { get; set; }

    /// <summary>See <see cref="AuditEventTypes"/> for valid values.</summary>
    public required string EventType { get; set; }

    public string? OldValue { get; set; }
    public string? NewValue { get; set; }

    public ActorType Actor { get; set; }

    /// <summary>Populated only when Actor == NivoraBusinessStaff.</summary>
    public Guid? ChangedByUserId { get; set; }

    /// <summary>Populated whenever the event is scoped to a specific
    /// conversation — not exclusive to Actor == Ai. VerificationCodeService
    /// already sets it for Actor == Customer, and
    /// ConversationTagCorrectionService (Step 22 Phase 3) sets it for
    /// Actor == NivoraBusinessStaff alongside ChangedByUserId.</summary>
    public Guid? ConversationId { get; set; }

    /// <summary>
    /// Links multiple AuditLog rows belonging to the same logical
    /// operation — e.g. VerificationCompleted → OrderCancellationExecuted
    /// for one cancelOrder attempt. Null for standalone events.
    /// </summary>
    public Guid? CorrelationId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

