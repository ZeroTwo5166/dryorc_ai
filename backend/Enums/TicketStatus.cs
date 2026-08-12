namespace backend.Enums;

public enum TicketStatus
{
    Open,
    InProgress,
    Resolved,

    /// <summary>
    /// Manual-only transition — application logic (TicketService) must
    /// enforce that a Resolved → Reopened move only happens via explicit
    /// staff action; nothing in the DB schema enforces this on its own.
    /// </summary>
    Reopened
}