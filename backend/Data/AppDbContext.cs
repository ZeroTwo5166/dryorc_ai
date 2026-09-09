using System.Reflection;
using backend.Constants;
using backend.Interfaces;
using backend.Models;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace backend.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options, ICurrentTenantAccessor tenantAccessor)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options), IDataProtectionKeyContext
{
    // Deliberately NOT "private readonly Guid? _currentBusinessId = tenantAccessor.BusinessId;"
    // (a one-time snapshot taken at construction). Identity's own
    // SecurityStampValidator resolves UserManager -> TenantAwareUserStore
    // -> AppDbContext from THIS SAME request's DI scope *during*
    // UseAuthentication() itself (CookieAuthenticationHandler.
    // HandleAuthenticateAsync -> SecurityStampValidator.ValidateAsync),
    // i.e. before HttpContext.User has been set to the authenticated
    // principal for this request. A value snapshotted at that moment would
    // freeze in "unauthenticated" for the entire request — including for
    // the controller, reusing the same scoped instance afterward, once
    // authentication has actually finished. Keeping the accessor itself
    // (not its value) and reading .BusinessId fresh inside the filter
    // means the filter re-evaluates it at each query's execution time,
    // by which point authentication has genuinely completed. Confirmed via
    // a stack trace captured at the point this was reproduced — not
    // guessed.
    private readonly ICurrentTenantAccessor _tenantAccessor = tenantAccessor;

    private static readonly MethodInfo ApplyTenantFilterMethod = typeof(AppDbContext)
        .GetMethod(nameof(ApplyTenantFilter), BindingFlags.NonPublic | BindingFlags.Instance)!;

    private void ApplyTenantFilter<TEntity>(ModelBuilder builder) where TEntity : class, ITenantOwned
    {
        builder.Entity<TEntity>().HasQueryFilter(e => e.BusinessId == _tenantAccessor.BusinessId);
    }

    public DbSet<Business> Businesses => Set<Business>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Widget> Widgets => Set<Widget>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<CustomerExtendedLoggingHistory> CustomerExtendedLoggingHistories => Set<CustomerExtendedLoggingHistory>();
    public DbSet<FlaggedCustomerActivityLog> FlaggedCustomerActivityLogs => Set<FlaggedCustomerActivityLog>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<KnowledgeDocument> KnowledgeDocuments => Set<KnowledgeDocument>();
    public DbSet<KnowledgeChunk> KnowledgeChunks => Set<KnowledgeChunk>();
    public DbSet<MessageKnowledgeSource> MessageKnowledgeSources => Set<MessageKnowledgeSource>();
    public DbSet<ExternalIdentity> ExternalIdentities => Set<ExternalIdentity>();
    public DbSet<Integration> Integrations => Set<Integration>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<SlaTarget> SlaTargets => Set<SlaTarget>();
    public DbSet<VerificationCode> VerificationCodes => Set<VerificationCode>();
    public DbSet<OrderCancellation> OrderCancellations => Set<OrderCancellation>();
    public DbSet<CustomerUpdateAction> CustomerUpdateActions => Set<CustomerUpdateAction>();
    public DbSet<TagModeMapping> TagModeMappings => Set<TagModeMapping>();
    public DbSet<Refund> Refunds => Set<Refund>();
    public DbSet<RefundItem> RefundItems => Set<RefundItem>();
    public DbSet<ConversationHandoff> ConversationHandoffs => Set<ConversationHandoff>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<NotificationRecipient> NotificationRecipients => Set<NotificationRecipient>();
    public DbSet<BusinessSigningKey> BusinessSigningKeys => Set<BusinessSigningKey>();
    public DbSet<ConsumedSignedIdentityToken> ConsumedSignedIdentityTokens => Set<ConsumedSignedIdentityToken>();
    public DbSet<Approval> Approvals => Set<Approval>();

    // Step 18: the ASP.NET Core Data Protection key ring, persisted here
    // (Postgres) instead of the framework's filesystem default — see
    // Program.cs's PersistKeysToDbContext registration for the operational
    // requirement this creates (this table must be in the production backup
    // scope). Framework-owned shape (DataProtectionKey has no BusinessId —
    // it is cross-tenant infrastructure, not an ITenantOwned entity, same
    // category as the inherited AspNetRoles/AspNetUsers tables).
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // ─────────────────────────────────────────────────────────────
        // Business
        // ─────────────────────────────────────────────────────────────
        builder.Entity<Business>(b =>
        {
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            b.Property(x => x.AutoRefundLimitCurrency).HasMaxLength(3);

            b.ToTable(t =>
            {
                t.HasCheckConstraint(
                    "CK_Business_RetentionDays_Range",
                    $"\"RetentionDays\" BETWEEN {RetentionPolicyLimits.MinDays} AND {RetentionPolicyLimits.MaxDays}");
                t.HasCheckConstraint(
                    "CK_Business_AutoRefundLimit_NonNegative",
                    "\"AutoRefundLimit\" IS NULL OR \"AutoRefundLimit\" >= 0");
                t.HasCheckConstraint(
                    "CK_Business_AutoRefundLimit_Pairing",
                    "(\"AutoRefundLimit\" IS NULL AND \"AutoRefundLimitCurrency\" IS NULL) " +
                    "OR (\"AutoRefundLimit\" IS NOT NULL AND \"AutoRefundLimitCurrency\" IS NOT NULL)");
            });
        });

        // ─────────────────────────────────────────────────────────────
        // AuditLog
        // ─────────────────────────────────────────────────────────────
        builder.Entity<AuditLog>(a =>
        {
            a.Property(x => x.Actor).HasConversion<string>().HasMaxLength(32);

            a.HasIndex(x => new { x.BusinessId, x.EntityType, x.EntityId, x.CreatedAt });
            a.HasIndex(x => new { x.BusinessId, x.CreatedAt });
            a.HasIndex(x => x.CorrelationId);

            a.HasOne<Business>().WithMany()
                .HasForeignKey(x => x.BusinessId)
                .OnDelete(DeleteBehavior.Restrict);

            a.HasOne<ApplicationUser>().WithMany()
                .HasForeignKey(x => x.ChangedByUserId)
                .OnDelete(DeleteBehavior.SetNull);

            a.HasOne<Conversation>().WithMany()
                .HasForeignKey(x => x.ConversationId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // ─────────────────────────────────────────────────────────────
        // ApplicationUser (Identity)
        // ─────────────────────────────────────────────────────────────
        builder.Entity<ApplicationUser>(u =>
        {
            u.HasIndex(x => x.BusinessId);
            u.HasOne<Business>().WithMany()
                .HasForeignKey(x => x.BusinessId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ─────────────────────────────────────────────────────────────
        // Widget
        // ─────────────────────────────────────────────────────────────
        builder.Entity<Widget>(w =>
        {
            w.HasIndex(x => x.BusinessId);
            w.HasIndex(x => x.PublicKey).IsUnique();

            w.HasOne<Business>().WithMany()
                .HasForeignKey(x => x.BusinessId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ─────────────────────────────────────────────────────────────
        // Customer
        // ─────────────────────────────────────────────────────────────
        builder.Entity<Customer>(c =>
        {
            c.HasIndex(x => x.BusinessId);
            c.HasIndex(x => new { x.BusinessId, x.ExternalId }).IsUnique();
            c.HasIndex(x => new { x.BusinessId, x.Email });

            c.HasOne<Business>().WithMany()
                .HasForeignKey(x => x.BusinessId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ─────────────────────────────────────────────────────────────
        // CustomerExtendedLoggingHistory
        // ─────────────────────────────────────────────────────────────
        builder.Entity<CustomerExtendedLoggingHistory>(h =>
        {
            h.HasIndex(x => new { x.BusinessId, x.CustomerId, x.EnabledAt });

            h.HasIndex(x => new { x.BusinessId, x.CustomerId })
                .IsUnique()
                .HasFilter("\"DisabledAt\" IS NULL");

            h.ToTable(t => t.HasCheckConstraint(
                "CK_CustomerExtendedLoggingHistory_DisabledPairing",
                "(\"DisabledByUserId\" IS NULL AND \"DisabledAt\" IS NULL) " +
                "OR (\"DisabledByUserId\" IS NOT NULL AND \"DisabledAt\" IS NOT NULL)"));

            h.HasOne<Business>().WithMany()
                .HasForeignKey(x => x.BusinessId)
                .OnDelete(DeleteBehavior.Restrict);

            h.HasOne<Customer>().WithMany()
                .HasForeignKey(x => x.CustomerId)
                .OnDelete(DeleteBehavior.Cascade);

            h.HasOne<ApplicationUser>().WithMany()
                .HasForeignKey(x => x.EnabledByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            h.HasOne<ApplicationUser>().WithMany()
                .HasForeignKey(x => x.DisabledByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // ─────────────────────────────────────────────────────────────
        // FlaggedCustomerActivityLog
        // ─────────────────────────────────────────────────────────────
        builder.Entity<FlaggedCustomerActivityLog>(c =>
        {
            c.Property(x => x.Actor).HasConversion<string>().HasMaxLength(32);

            c.HasIndex(x => new { x.BusinessId, x.CustomerId, x.CreatedAt });

            c.HasOne<Business>().WithMany()
                .HasForeignKey(x => x.BusinessId)
                .OnDelete(DeleteBehavior.Restrict);

            c.HasOne<Customer>().WithMany()
                .HasForeignKey(x => x.CustomerId)
                .OnDelete(DeleteBehavior.Cascade);

            c.HasOne<Conversation>().WithMany()
                .HasForeignKey(x => x.ConversationId)
                .OnDelete(DeleteBehavior.SetNull);

            c.HasOne<ApplicationUser>().WithMany()
                .HasForeignKey(x => x.PerformedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // ─────────────────────────────────────────────────────────────
        // Conversation
        // ─────────────────────────────────────────────────────────────
        builder.Entity<Conversation>(c =>
        {
            c.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            c.Property(x => x.HandledBy).HasConversion<string>().HasMaxLength(32);
            c.Property(x => x.Channel).HasMaxLength(32);

            c.HasIndex(x => new { x.BusinessId, x.CreatedAt });
            c.HasIndex(x => new { x.BusinessId, x.CustomerId });
            c.HasIndex(x => x.VisitorSessionId);
            c.HasIndex(x => new { x.BusinessId, x.LastMessageAt });

            c.HasOne<Business>().WithMany()
                .HasForeignKey(x => x.BusinessId)
                .OnDelete(DeleteBehavior.Restrict);

            c.HasOne<Widget>().WithMany()
                .HasForeignKey(x => x.WidgetId)
                .OnDelete(DeleteBehavior.Restrict);

            c.HasOne<Customer>().WithMany()
                .HasForeignKey(x => x.CustomerId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // ─────────────────────────────────────────────────────────────
        // Message
        // ─────────────────────────────────────────────────────────────
        builder.Entity<Message>(m =>
        {
            m.Property(x => x.Role).HasConversion<string>().HasMaxLength(32);

            m.HasIndex(x => new { x.BusinessId, x.ConversationId, x.CreatedAt });
            m.HasIndex(x => new { x.BusinessId, x.CreatedAt });
            m.HasIndex(x => x.AuthorUserId);

            m.ToTable(t => t.HasCheckConstraint(
                "CK_Messages_AuthorUserId_RolePairing",
                "(\"Role\" = 'Agent' AND \"AuthorUserId\" IS NOT NULL) " +
                "OR (\"Role\" != 'Agent' AND \"AuthorUserId\" IS NULL)"));

            m.HasOne<Business>().WithMany()
                .HasForeignKey(x => x.BusinessId)
                .OnDelete(DeleteBehavior.Restrict);

            m.HasOne<Conversation>().WithMany()
                .HasForeignKey(x => x.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);

            m.HasOne<ApplicationUser>().WithMany()
                .HasForeignKey(x => x.AuthorUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // ─────────────────────────────────────────────────────────────
        // KnowledgeDocument
        // ─────────────────────────────────────────────────────────────
        builder.Entity<KnowledgeDocument>(k =>
        {
            k.Property(x => x.Type).HasConversion<string>().HasMaxLength(32);

            k.HasIndex(x => x.BusinessId);
            k.HasIndex(x => new { x.BusinessId, x.UpdatedAt });

            k.HasOne<Business>().WithMany()
                .HasForeignKey(x => x.BusinessId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ─────────────────────────────────────────────────────────────
        // KnowledgeChunk
        // ─────────────────────────────────────────────────────────────
        builder.Entity<KnowledgeChunk>(c =>
        {
            c.Property(x => x.Embedding).HasColumnType($"vector({EmbeddingDimensions.VoyageThree})");

            c.HasIndex(x => x.BusinessId);
            c.HasIndex(x => new { x.KnowledgeDocumentId, x.ChunkIndex }).IsUnique();

            c.HasIndex(x => x.Embedding)
                .HasMethod("hnsw")
                .HasOperators("vector_cosine_ops");

            c.HasOne<Business>().WithMany()
                .HasForeignKey(x => x.BusinessId)
                .OnDelete(DeleteBehavior.Restrict);

            c.HasOne<KnowledgeDocument>().WithMany()
                .HasForeignKey(x => x.KnowledgeDocumentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ─────────────────────────────────────────────────────────────
        // MessageKnowledgeSource
        // ─────────────────────────────────────────────────────────────
        builder.Entity<MessageKnowledgeSource>(s =>
        {
            s.HasIndex(x => new { x.BusinessId, x.MessageId });

            s.HasOne<Business>().WithMany()
                .HasForeignKey(x => x.BusinessId)
                .OnDelete(DeleteBehavior.Restrict);

            s.HasOne<Message>().WithMany()
                .HasForeignKey(x => x.MessageId)
                .OnDelete(DeleteBehavior.Cascade);

            s.HasOne<KnowledgeDocument>().WithMany()
                .HasForeignKey(x => x.KnowledgeDocumentId)
                .OnDelete(DeleteBehavior.SetNull);

            s.HasOne<KnowledgeChunk>().WithMany()
                .HasForeignKey(x => x.KnowledgeChunkId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // ─────────────────────────────────────────────────────────────
        // ExternalIdentity
        // ─────────────────────────────────────────────────────────────
        builder.Entity<ExternalIdentity>(i =>
        {
            i.Property(x => x.Channel).HasMaxLength(32);
            i.Property(x => x.VerificationMethod).HasMaxLength(32);

            i.HasIndex(x => new { x.BusinessId, x.Channel, x.ExternalUserId })
                .IsUnique()
                .HasFilter("\"RevokedAt\" IS NULL");

            i.HasIndex(x => new { x.BusinessId, x.CustomerId });

            i.ToTable(t => t.HasCheckConstraint(
                "CK_ExternalIdentities_VerificationMethod",
                "\"VerificationMethod\" IN ('SignedToken', 'Otp')"));

            i.HasOne<Business>().WithMany()
                .HasForeignKey(x => x.BusinessId)
                .OnDelete(DeleteBehavior.Restrict);

            i.HasOne<Customer>().WithMany()
                .HasForeignKey(x => x.CustomerId)
                .OnDelete(DeleteBehavior.Cascade);

            i.HasOne<ApplicationUser>().WithMany()
                .HasForeignKey(x => x.RevokedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // ─────────────────────────────────────────────────────────────
        // BusinessSigningKey (Step 21, signed-token half)
        // ─────────────────────────────────────────────────────────────
        builder.Entity<BusinessSigningKey>(k =>
        {
            k.Property(x => x.Algorithm).HasMaxLength(16);

            // Non-unique — multiple concurrently-active keys per business
            // are legal and expected during rotation.
            k.HasIndex(x => x.BusinessId);

            k.HasOne<Business>().WithMany()
                .HasForeignKey(x => x.BusinessId)
                .OnDelete(DeleteBehavior.Restrict);

            k.HasOne<ApplicationUser>().WithMany()
                .HasForeignKey(x => x.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ─────────────────────────────────────────────────────────────
        // ConsumedSignedIdentityToken (Step 21, signed-token half)
        // ─────────────────────────────────────────────────────────────
        builder.Entity<ConsumedSignedIdentityToken>(t =>
        {
            t.Property(x => x.Jti).HasMaxLength(256);

            // The sole replay-protection arbiter — see
            // SignedIdentityTokenService's insert-first sequencing.
            t.HasIndex(x => new { x.BusinessId, x.Jti }).IsUnique();

            t.HasOne<Business>().WithMany()
                .HasForeignKey(x => x.BusinessId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ─────────────────────────────────────────────────────────────
        // Integration
        // ─────────────────────────────────────────────────────────────
        builder.Entity<Integration>(i =>
        {
            i.Property(x => x.Platform).HasMaxLength(32);

            i.HasIndex(x => new { x.BusinessId, x.Platform }).IsUnique();

            i.HasIndex(x => new { x.Platform, x.ExternalAccountIdentifier })
                .IsUnique()
                .HasFilter("\"ExternalAccountIdentifier\" IS NOT NULL");

            i.HasOne<Business>().WithMany()
                .HasForeignKey(x => x.BusinessId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ─────────────────────────────────────────────────────────────
        // Ticket
        // ─────────────────────────────────────────────────────────────
        builder.Entity<Ticket>(t =>
        {
            t.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            t.Property(x => x.Priority).HasConversion<string>().HasMaxLength(32);
            t.Property(x => x.PriorityReason).HasMaxLength(32);
            t.Property(x => x.Channel).HasMaxLength(32);

            t.HasIndex(x => new { x.BusinessId, x.Status, x.Priority, x.CreatedAt });
            t.HasIndex(x => new { x.BusinessId, x.CustomerId });
            t.HasIndex(x => new { x.BusinessId, x.ClaimedByUserId });
            t.HasIndex(x => x.ResolvedByUserId);

            // Step 16: at most one non-Resolved ticket per conversation.
            // NULLs are already exempt from uniqueness under standard SQL
            // semantics (ConversationId IS NULL rows never conflict with
            // each other), so conversationless tickets coexist freely
            // without special-casing — the explicit "IS NOT NULL" clause
            // below is included anyway to match Integration's own
            // (Platform, ExternalAccountIdentifier) precedent for a unique
            // index over a nullable column, not because it's required for
            // correctness here.
            t.HasIndex(x => x.ConversationId)
                .IsUnique()
                .HasDatabaseName("IX_Tickets_ActiveTicketPerConversation")
                .HasFilter("\"ConversationId\" IS NOT NULL AND \"Status\" <> 'Resolved'");

            t.ToTable(tb =>
            {
                tb.HasCheckConstraint(
                    "CK_Tickets_PriorityReason",
                    "\"PriorityReason\" IN ('RefundDispute', 'ManualEscalation', 'RefundLimitReached')");

                tb.HasCheckConstraint(
                    "CK_Tickets_FirstRespondedAt_After_CreatedAt",
                    "\"FirstRespondedAt\" IS NULL OR \"FirstRespondedAt\" >= \"CreatedAt\"");
            });

            t.HasOne<Business>().WithMany()
                .HasForeignKey(x => x.BusinessId)
                .OnDelete(DeleteBehavior.Restrict);

            t.HasOne<Customer>().WithMany()
                .HasForeignKey(x => x.CustomerId)
                .OnDelete(DeleteBehavior.Restrict);

            t.HasOne<Conversation>().WithMany()
                .HasForeignKey(x => x.ConversationId)
                .OnDelete(DeleteBehavior.SetNull);

            t.HasOne<ApplicationUser>().WithMany()
                .HasForeignKey(x => x.ClaimedByUserId)
                .OnDelete(DeleteBehavior.SetNull);

            t.HasOne<ApplicationUser>().WithMany()
                .HasForeignKey(x => x.ResolvedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // ─────────────────────────────────────────────────────────────
        // SlaTarget
        // ─────────────────────────────────────────────────────────────
        builder.Entity<SlaTarget>(s =>
        {
            s.Property(x => x.Priority).HasConversion<string>().HasMaxLength(32);
            s.Property(x => x.Channel).HasMaxLength(32);

            s.HasIndex(x => new { x.BusinessId, x.Priority, x.Channel }).IsUnique();

            s.HasIndex(x => new { x.BusinessId, x.Priority })
                .IsUnique()
                .HasFilter("\"Channel\" IS NULL");

            s.ToTable(t => t.HasCheckConstraint(
                "CK_SlaTargets_TargetMinutes_Positive",
                "\"TargetMinutes\" > 0"));

            s.HasOne<Business>().WithMany()
                .HasForeignKey(x => x.BusinessId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ─────────────────────────────────────────────────────────────
        // VerificationCode
        // ─────────────────────────────────────────────────────────────
        builder.Entity<VerificationCode>(v =>
        {
            v.Property(x => x.Channel).HasMaxLength(32);

            v.HasIndex(x => new { x.BusinessId, x.ConversationId });

            v.HasIndex(x => new
            {
                x.BusinessId,
                x.CustomerId,
                x.ConversationId,
                x.Channel,
                x.CreatedAt
            });

            v.ToTable(t => t.HasCheckConstraint(
                "CK_VerificationCodes_Attempts",
                "\"AttemptCount\" >= 0 " +
                "AND \"MaxAttempts\" > 0 " +
                "AND \"AttemptCount\" <= \"MaxAttempts\""));

            v.HasOne<Business>().WithMany()
                .HasForeignKey(x => x.BusinessId)
                .OnDelete(DeleteBehavior.Restrict);

            v.HasOne<Customer>().WithMany()
                .HasForeignKey(x => x.CustomerId)
                .OnDelete(DeleteBehavior.Cascade);

            v.HasOne<Conversation>().WithMany()
                .HasForeignKey(x => x.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ─────────────────────────────────────────────────────────────
        // OrderCancellation
        // ─────────────────────────────────────────────────────────────
        builder.Entity<OrderCancellation>(o =>
        {
            o.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            o.Property(x => x.Reason).HasConversion<string>().HasMaxLength(32);
            o.Property(x => x.Method).HasConversion<string>().HasMaxLength(32);
            o.Property(x => x.ExternalOrderId).HasMaxLength(256);
            o.Property(x => x.IdempotencyKey).HasMaxLength(256);

            // Explicit name on this first index via the HasIndex(expr, name)
            // overload — previously unnamed, relying on EF Core's own
            // default-naming convention — is now required, not stylistic:
            // Step 23S adds a second index over these exact same
            // (BusinessId, ExternalOrderId) columns below. Confirmed
            // directly this pass, empirically: chaining .HasDatabaseName(...)
            // after a second, otherwise-identical HasIndex(propertyExpression)
            // call does NOT create two independent index objects — EF Core's
            // fluent builder silently merges the second call's configuration
            // into the first's single underlying index definition regardless
            // (verified by inspecting AppDbContextModelSnapshot.cs after a
            // first attempt: only one index over these columns existed in
            // the generated model at all), and the resulting migration
            // dropped this pre-existing, unrelated index — something this
            // task was never authorized to remove. The HasIndex(expression,
            // name) overload below, giving each call an explicit name at the
            // point of construction rather than via a later chained call, is
            // the mechanism that actually produces two distinct, independently-
            // managed index objects.
            o.HasIndex(x => new { x.BusinessId, x.ExternalOrderId }, "IX_OrderCancellations_BusinessId_ExternalOrderId");
            o.HasIndex(x => new { x.BusinessId, x.CustomerId });
            o.HasIndex(x => new { x.BusinessId, x.IdempotencyKey }).IsUnique();

            // Step 23S — at most one Pending OrderCancellation per order at a
            // time, closing the gap Step 22R first designed (two different
            // Approvals independently reaching Execute for the same order).
            o.HasIndex(x => new { x.BusinessId, x.ExternalOrderId }, "IX_OrderCancellations_BusinessId_ExternalOrderId_Pending")
                .IsUnique()
                .HasFilter("\"Status\" = 'Pending'");

            o.ToTable(t => t.HasCheckConstraint(
                "CK_OrderCancellations_CompletedAt",
                """
                ("Status" = 'Confirmed' AND "CompletedAt" IS NOT NULL)
                OR
                ("Status" <> 'Confirmed' AND "CompletedAt" IS NULL)
                """));

            o.HasOne<Business>().WithMany()
                .HasForeignKey(x => x.BusinessId)
                .OnDelete(DeleteBehavior.Restrict);

            o.HasOne<Customer>().WithMany()
                .HasForeignKey(x => x.CustomerId)
                .OnDelete(DeleteBehavior.Restrict);

            o.HasOne<Conversation>().WithMany()
                .HasForeignKey(x => x.ConversationId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // ─────────────────────────────────────────────────────────────
        // CustomerUpdateAction
        // ─────────────────────────────────────────────────────────────
        builder.Entity<CustomerUpdateAction>(u =>
        {
            u.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            u.Property(x => x.IdempotencyKey).HasMaxLength(256);
            u.Property(x => x.FieldsChanged).HasColumnType("text[]");

            u.HasIndex(x => new { x.BusinessId, x.CustomerId });
            u.HasIndex(x => new { x.BusinessId, x.ConversationId });
            u.HasIndex(x => new { x.BusinessId, x.IdempotencyKey }).IsUnique();

            u.ToTable(t =>
            {
                t.HasCheckConstraint(
                    "CK_CustomerUpdateActions_FieldsChanged",
                    "cardinality(\"FieldsChanged\") > 0");
                t.HasCheckConstraint(
                    "CK_CustomerUpdateActions_CompletedAt",
                    """
                    ("Status" = 'Confirmed' AND "CompletedAt" IS NOT NULL)
                    OR
                    ("Status" <> 'Confirmed' AND "CompletedAt" IS NULL)
                    """);
            });

            u.HasOne<Business>().WithMany()
                .HasForeignKey(x => x.BusinessId)
                .OnDelete(DeleteBehavior.Restrict);

            u.HasOne<Customer>().WithMany()
                .HasForeignKey(x => x.CustomerId)
                .OnDelete(DeleteBehavior.Restrict);

            u.HasOne<Conversation>().WithMany()
                .HasForeignKey(x => x.ConversationId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // ─────────────────────────────────────────────────────────────
        // TagModeMapping
        // ─────────────────────────────────────────────────────────────
        builder.Entity<TagModeMapping>(tm =>
        {
            tm.Property(x => x.Mode).HasConversion<string>().HasMaxLength(32);
            tm.Property(x => x.Tag).HasMaxLength(64);

            tm.HasIndex(x => new { x.BusinessId, x.Tag }).IsUnique();

            tm.ToTable(t => t.HasCheckConstraint(
                "CK_TagModeMappings_Tag_NotEmpty",
                "length(trim(\"Tag\")) > 0"));

            tm.HasOne<Business>().WithMany()
                .HasForeignKey(x => x.BusinessId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ─────────────────────────────────────────────────────────────
        // Refund
        // ─────────────────────────────────────────────────────────────
        builder.Entity<Refund>(r =>
        {
            r.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            r.Property(x => x.Method).HasConversion<string>().HasMaxLength(32);
            r.Property(x => x.Currency).HasMaxLength(3);
            r.Property(x => x.ExternalOrderId).HasMaxLength(256);
            r.Property(x => x.IdempotencyKey).HasMaxLength(256);

            r.HasIndex(x => new { x.BusinessId, x.ExternalOrderId });
            r.HasIndex(x => new { x.BusinessId, x.CustomerId });
            r.HasIndex(x => new { x.BusinessId, x.IdempotencyKey }).IsUnique();

            r.ToTable(t =>
            {
                t.HasCheckConstraint(
                    "CK_Refunds_TotalAmount_NonNegative",
                    "\"TotalAmount\" IS NULL OR \"TotalAmount\" >= 0");
                t.HasCheckConstraint(
                    "CK_Refunds_CompletedAt",
                    """
                    ("Status" = 'Confirmed' AND "CompletedAt" IS NOT NULL)
                    OR
                    ("Status" <> 'Confirmed' AND "CompletedAt" IS NULL)
                    """);
            });

            r.HasOne<Business>().WithMany()
                .HasForeignKey(x => x.BusinessId)
                .OnDelete(DeleteBehavior.Restrict);

            r.HasOne<Customer>().WithMany()
                .HasForeignKey(x => x.CustomerId)
                .OnDelete(DeleteBehavior.Restrict);

            r.HasOne<Conversation>().WithMany()
                .HasForeignKey(x => x.ConversationId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // ─────────────────────────────────────────────────────────────
        // RefundItem
        // ─────────────────────────────────────────────────────────────
        builder.Entity<RefundItem>(ri =>
        {
            ri.Property(x => x.ExternalLineItemId).HasMaxLength(256);

            ri.HasIndex(x => new { x.BusinessId, x.RefundId, x.ExternalLineItemId }).IsUnique();

            ri.ToTable(t => t.HasCheckConstraint(
                "CK_RefundItems_Amount_Positive",
                "\"Amount\" > 0 AND \"Quantity\" > 0"));

            ri.HasOne<Business>().WithMany()
                .HasForeignKey(x => x.BusinessId)
                .OnDelete(DeleteBehavior.Restrict);

            ri.HasOne<Refund>().WithMany()
                .HasForeignKey(x => x.RefundId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ─────────────────────────────────────────────────────────────
        // ConversationHandoff
        // ─────────────────────────────────────────────────────────────
        builder.Entity<ConversationHandoff>(ch =>
        {
            ch.Property(x => x.FromHandledBy).HasConversion<string>().HasMaxLength(32);
            ch.Property(x => x.ToHandledBy).HasConversion<string>().HasMaxLength(32);
            ch.Property(x => x.Reason).HasMaxLength(32);
            ch.Property(x => x.ActorType).HasConversion<string>().HasMaxLength(32);

            ch.HasIndex(x => new { x.BusinessId, x.ConversationId, x.CreatedAt });
            ch.HasIndex(x => new { x.BusinessId, x.TicketId });
            ch.HasIndex(x => new { x.BusinessId, x.ActorId });

            ch.ToTable(t => t.HasCheckConstraint(
                "CK_ConversationHandoffs_Actor",
                """
                ("ActorType" = 'NivoraBusinessStaff' AND "ActorId" IS NOT NULL)
                OR
                ("ActorType" <> 'NivoraBusinessStaff' AND "ActorId" IS NULL)
                """));

            ch.HasOne<Business>().WithMany()
                .HasForeignKey(x => x.BusinessId)
                .OnDelete(DeleteBehavior.Restrict);

            ch.HasOne<Conversation>().WithMany()
                .HasForeignKey(x => x.ConversationId)
                .OnDelete(DeleteBehavior.SetNull);

            ch.HasOne<Ticket>().WithMany()
                .HasForeignKey(x => x.TicketId)
                .OnDelete(DeleteBehavior.SetNull);

            ch.HasOne<ApplicationUser>().WithMany()
                .HasForeignKey(x => x.ActorId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // ─────────────────────────────────────────────────────────────
        // Notification
        // ─────────────────────────────────────────────────────────────
        builder.Entity<Notification>(n =>
        {
            n.Property(x => x.Category).HasConversion<string>().HasMaxLength(32);
            n.Property(x => x.Type).HasMaxLength(64);
            n.Property(x => x.ActorType).HasConversion<string>().HasMaxLength(32);
            n.Property(x => x.IdempotencyKey).HasMaxLength(256);

            n.HasIndex(x => new { x.BusinessId, x.Category, x.CreatedAt });

            // Composite, not a bare ConversationId index — every other
            // index in this schema leads with BusinessId (Section 11: a
            // standalone trailing-field index is redundant once a
            // BusinessId-led composite exists), and the purge sweep's own
            // delete filters on exactly (BusinessId, ConversationId), same
            // shape as ConversationPurgeService's existing Messages delete.
            n.HasIndex(x => new { x.BusinessId, x.ConversationId })
                .HasFilter("\"ConversationId\" IS NOT NULL");

            n.HasIndex(x => new { x.BusinessId, x.IdempotencyKey })
                .IsUnique()
                .HasFilter("\"IdempotencyKey\" IS NOT NULL");

            n.ToTable(t =>
            {
                t.HasCheckConstraint(
                    "CK_Notifications_Actor",
                    """
                    ("ActorType" = 'NivoraBusinessStaff' AND "ActorUserId" IS NOT NULL)
                    OR
                    ("ActorType" <> 'NivoraBusinessStaff' AND "ActorUserId" IS NULL)
                    """);

                t.HasCheckConstraint(
                    "CK_Notifications_CategoryRefs",
                    """
                    "Category" <> 'Team'
                    OR
                    ("ConversationId" IS NULL AND "TicketId" IS NULL)
                    """);

                // Must stay synchronized with NotificationTypes and
                // NotificationCategoryMap — see NotificationCategoryMap's
                // doc comment for why all three exist together.
                t.HasCheckConstraint(
                    "CK_Notifications_CategoryTypeConsistency",
                    """
                    ("Category" = 'Team' AND "Type" IN ('TeamMemberJoined', 'StaffRoleChanged', 'StaffAccessRevoked'))
                    OR
                    ("Category" = 'Customer' AND "Type" IN ('ConversationAssigned', 'TicketSlaWarning', 'CustomerReplied', 'NewConversation', 'ConversationEscalated'))
                    OR
                    ("Category" = 'System' AND "Type" IN ('KnowledgeImportCompleted', 'IntegrationSyncCompleted'))
                    """);
            });

            n.HasOne<Business>().WithMany()
                .HasForeignKey(x => x.BusinessId)
                .OnDelete(DeleteBehavior.Restrict);

            // SetNull, not Cascade — Conversation rows are never actually
            // deleted (ConversationPurgeService anonymizes them in place),
            // so this FK's Cascade would never fire; ConversationPurgeService
            // explicitly deletes matching Notification rows itself, in the
            // same transaction as its Messages delete, before anonymizing
            // the Conversation. This FK is only a safety net for a
            // Conversation-row deletion no current code path performs.
            n.HasOne<Conversation>().WithMany()
                .HasForeignKey(x => x.ConversationId)
                .OnDelete(DeleteBehavior.SetNull);

            n.HasOne<Ticket>().WithMany()
                .HasForeignKey(x => x.TicketId)
                .OnDelete(DeleteBehavior.SetNull);

            n.HasOne<ApplicationUser>().WithMany()
                .HasForeignKey(x => x.ActorUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // ─────────────────────────────────────────────────────────────
        // NotificationRecipient
        // ─────────────────────────────────────────────────────────────
        builder.Entity<NotificationRecipient>(r =>
        {
            r.HasIndex(x => new { x.NotificationId, x.UserId }).IsUnique();

            // Trailing Id is mandatory — the feed paginates on
            // (CreatedAt, Id) keyset, and a fan-out batch shares one
            // CreatedAt value across every recipient row, so ties are
            // real, not a theoretical edge case.
            r.HasIndex(x => new { x.BusinessId, x.UserId, x.CreatedAt, x.Id });

            r.HasIndex(x => new { x.BusinessId, x.UserId })
                .HasFilter("\"IsRead\" = false");

            r.HasOne<Business>().WithMany()
                .HasForeignKey(x => x.BusinessId)
                .OnDelete(DeleteBehavior.Restrict);

            // Cascade — unlike Conversation, a Notification row really is
            // deleted (both by ConversationPurgeService and, in principle,
            // any future direct delete), so this FK's Cascade genuinely
            // fires and is the correct mechanism for removing the fan-out
            // rows with it. Never delete NotificationRecipient rows
            // manually — this FK is the single removal path.
            r.HasOne<Notification>().WithMany()
                .HasForeignKey(x => x.NotificationId)
                .OnDelete(DeleteBehavior.Cascade);

            r.HasOne<ApplicationUser>().WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ─────────────────────────────────────────────────────────────
        // Approval — Step 22 AI Action Safety Model. Persistence only; see
        // Approval.cs's own doc comment.
        // ─────────────────────────────────────────────────────────────
        builder.Entity<Approval>(ap =>
        {
            ap.Property(x => x.ToolName).HasMaxLength(256);
            ap.Property(x => x.ResourceId).HasMaxLength(256);
            ap.Property(x => x.ActionHash).HasMaxLength(64);
            ap.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);

            ap.HasOne<Business>().WithMany()
                .HasForeignKey(x => x.BusinessId)
                .OnDelete(DeleteBehavior.Restrict);

            // Cascade, not Restrict — ConversationId is required non-null
            // here (unlike every other entity's own nullable ConversationId,
            // which uses SetNull instead, since SetNull is structurally
            // impossible against a non-nullable column). The only other two
            // non-nullable ConversationId FKs in this codebase (Message,
            // VerificationCode) both use Cascade — matched here per
            // docs/handoffs/step-22w-approval-migration-design-independent-verification.md
            // §7's independently-verified, correctly-scoped precedent.
            ap.HasOne<Conversation>().WithMany()
                .HasForeignKey(x => x.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);

            ap.HasOne<Customer>().WithMany()
                .HasForeignKey(x => x.CustomerId)
                .OnDelete(DeleteBehavior.Restrict);

            ap.HasOne<ApplicationUser>().WithMany()
                .HasForeignKey(x => x.ApprovedBy)
                .OnDelete(DeleteBehavior.SetNull);

            ap.HasOne<ApplicationUser>().WithMany()
                .HasForeignKey(x => x.DeniedBy)
                .OnDelete(DeleteBehavior.SetNull);

            ap.HasOne<OrderCancellation>().WithMany()
                .HasForeignKey(x => x.OrderCancellationId)
                .OnDelete(DeleteBehavior.Restrict);

            // Step refundOrder Phase 1 — identical shape to the
            // OrderCancellationId FK immediately above (nullable,
            // Restrict-on-delete). See design doc §12.3.
            ap.HasOne<Refund>().WithMany()
                .HasForeignKey(x => x.RefundId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ─────────────────────────────────────────────────────────────
        // Global multi-tenancy query filter — every ITenantOwned entity
        // (including ApplicationUser via Identity) is scoped to
        // _currentBusinessId on every read. Applied last so it composes
        // with, rather than replaces, the per-entity configuration above.
        // Write-path tenant checks are NOT covered by this — see
        // NIVORA_SCHEMA_AND_LOGIC_REFERENCE.md Section 1, principle 3.
        // ─────────────────────────────────────────────────────────────
        foreach (var entityType in builder.Model.GetEntityTypes())
        {
            if (typeof(ITenantOwned).IsAssignableFrom(entityType.ClrType))
            {
                ApplyTenantFilterMethod.MakeGenericMethod(entityType.ClrType).Invoke(this, [builder]);
            }
        }
    }
}