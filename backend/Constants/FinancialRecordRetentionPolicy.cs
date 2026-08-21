namespace backend.Constants;

/// <summary>
/// Governs OrderCancellation, CustomerUpdateAction, and Refund — records
/// GDPR Art. 17(3)(b) exempts from the erasure right while a legal
/// retention obligation applies (financial/transaction records are the
/// textbook case; most jurisdictions require multi-year bookkeeping
/// retention regardless of a data subject's erasure request). This is
/// exactly why CustomerErasureService/ConversationPurgeService never touch
/// these three tables — not because they're "permanently retained" (they
/// are not, and no code should claim that), but because their retention is
/// governed by a separate legal clock, not the conversation-retention one.
///
/// A single centralized constant, not three independent per-entity
/// columns/fields — all three tables share the same legal category
/// (financial/transaction records) and there's no product reason for them
/// to diverge, so one policy value is the least-disruptive representation:
/// no migration, no new Business-configurable column (this isn't a
/// business preference the way Business.RetentionDays is — a business
/// should not be able to configure its way out of a statutory bookkeeping
/// requirement).
///
/// TODO(legal): RetentionDays below is a placeholder, NOT a confirmed
/// figure — it needs jurisdiction-specific legal/accounting confirmation
/// (bookkeeping retention requirements vary roughly 5-10 years across
/// jurisdictions; Nivora is Denmark-based per the dev plan, where the
/// Bogføringsloven currently specifies 5 years) before any enforcement job
/// is built against it. No enforcement job exists yet — this constant only
/// exists so the eventual job has one place to read from, and so nothing
/// in code implies these records live forever.
/// </summary>
public static class FinancialRecordRetentionPolicy
{
    public const int RetentionDays = 1825; // ~5 years — placeholder, see TODO above
}
