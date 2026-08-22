namespace backend.Services;

/// <summary>
/// Structural defense against PII entering Notification.Title:
/// NotificationService.NotifyAsync accepts one of these typed subtypes
/// instead of a free-form Dictionary&lt;string, object&gt; or a raw
/// pre-rendered string, so a future caller cannot casually pass
/// Customer.Name/Email/ExternalId/a raw message excerpt through an untyped
/// bag — each subtype's fields are narrow and purpose-built, not a general
/// key-value store.
///
/// This is a structural deterrent, not an absolute guarantee: nothing stops
/// a future subtype from being given a free-text field that itself holds
/// PII. It only removes the easy, accidental path.
///
/// There are no live callers yet, so these subtypes are scoped from each
/// NotificationTypes value's own semantic meaning, not from any actual
/// caller's real data needs — see NotificationService for the Type ->
/// expected-subtype mapping this relies on.
/// </summary>
public abstract record NotificationTitleArgs
{
    /// <summary>Renders this into the exact string stored as Notification.Title.</summary>
    public abstract string Render();
}

public sealed record TeamMemberJoinedArgs : NotificationTitleArgs
{
    public override string Render() => "A new team member joined your business.";
}

public sealed record StaffRoleChangedArgs(string NewRoleName) : NotificationTitleArgs
{
    public override string Render() => $"A staff member's role changed to {NewRoleName}.";
}

public sealed record StaffAccessRevokedArgs : NotificationTitleArgs
{
    public override string Render() => "A staff member's access was revoked.";
}

public sealed record ConversationAssignedArgs : NotificationTitleArgs
{
    public override string Render() => "A conversation was assigned to you.";
}

public sealed record TicketSlaWarningArgs(int MinutesRemaining) : NotificationTitleArgs
{
    public override string Render() => $"A ticket is close to its SLA deadline ({MinutesRemaining} min remaining).";
}

public sealed record CustomerRepliedArgs : NotificationTitleArgs
{
    public override string Render() => "A customer sent a new message.";
}

public sealed record NewConversationArgs : NotificationTitleArgs
{
    public override string Render() => "A new conversation was started.";
}

public sealed record ConversationEscalatedArgs : NotificationTitleArgs
{
    public override string Render() => "A conversation was escalated to a human.";
}

public sealed record KnowledgeImportCompletedArgs(string DocumentTitle) : NotificationTitleArgs
{
    public override string Render() => $"Knowledge import completed: {DocumentTitle}.";
}

public sealed record IntegrationSyncCompletedArgs(string PlatformName) : NotificationTitleArgs
{
    public override string Render() => $"{PlatformName} sync completed.";
}
