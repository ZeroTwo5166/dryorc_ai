namespace backend.Constants;

/// <summary>
/// String constants for Ticket.PriorityReason. Unlike most string+constants
/// fields (EntityType, EventType, Channel, HandoffReason), this one is
/// backed by a DB-level CHECK constraint (CK_Tickets_PriorityReason) —
/// trading away migration-free extensibility for DB-level enforcement,
/// since the set is small and closed. Whenever a new reason is added
/// here, CK_Tickets_PriorityReason must be updated in the same migration.
/// </summary>
public static class TicketPriorityReasons
{
    public const string RefundDispute = "RefundDispute";
    public const string ManualEscalation = "ManualEscalation";
    public const string RefundLimitReached = "RefundLimitReached";
}