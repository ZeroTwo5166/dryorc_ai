using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Data;
using System.Diagnostics;
using backend.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace backend.Tests;

/// <summary>
/// Shared cleanup for test classes that create Businesses. A test hands over
/// the Business Ids it created; the helper deletes the rows those Businesses
/// own, in one transaction on its own connection, and nothing else.
/// See docs/handoffs/test-cleanup-helper-design.md (v6) and its errata.
///
/// Sequence per call (design section 6.1): validate before connecting; round trip 1
/// (SET LOCAL timeouts, lock the listed Businesses rows, creation-time check,
/// cross-owner check); round trip 2 (the 34 statements as one batch); commit.
/// Any refusal or failure rolls back and throws; nothing is retried or swallowed.
/// </summary>
internal static class TestBusinessCleanup
{
    // K2 deny list: the three owner Businesses.
    internal static readonly FrozenSet<Guid> OwnerBusinessIds = new[]
    {
        Guid.Parse("b2636ee3-0e5a-4c84-af4a-725ef9b659cd"),
        Guid.Parse("28e0c251-d4a4-49f3-9e2a-4905bb23a7cf"),
        Guid.Parse("50903e70-54bd-471a-ba7c-bd2344c6a5c3"),
    }.ToFrozenSet();

    // The 34 statements, in order, each with the one uuid[] parameter @ids.
    internal static readonly ImmutableArray<CleanupStatement> Statements =
    [
        new(1, "MessageKnowledgeSources", """DELETE FROM "MessageKnowledgeSources" WHERE "BusinessId" = ANY(@ids)"""),
        new(2, "KnowledgeChunks", """DELETE FROM "KnowledgeChunks" WHERE "BusinessId" = ANY(@ids)"""),
        new(3, "KnowledgeDocuments", """DELETE FROM "KnowledgeDocuments" WHERE "BusinessId" = ANY(@ids)"""),
        new(4, "Messages", """DELETE FROM "Messages" WHERE "BusinessId" = ANY(@ids)"""),
        new(5, "Approvals", """DELETE FROM "Approvals" WHERE "BusinessId" = ANY(@ids)"""),
        new(6, "RefundItems", """DELETE FROM "RefundItems" WHERE "BusinessId" = ANY(@ids)"""),
        new(7, "Refunds", """DELETE FROM "Refunds" WHERE "BusinessId" = ANY(@ids)"""),
        new(8, "OrderCancellations", """DELETE FROM "OrderCancellations" WHERE "BusinessId" = ANY(@ids)"""),
        new(9, "NotificationRecipients", """DELETE FROM "NotificationRecipients" WHERE "BusinessId" = ANY(@ids)"""),
        new(10, "Notifications", """DELETE FROM "Notifications" WHERE "BusinessId" = ANY(@ids)"""),
        new(11, "ConversationHandoffs", """DELETE FROM "ConversationHandoffs" WHERE "BusinessId" = ANY(@ids)"""),
        new(12, "Tickets", """DELETE FROM "Tickets" WHERE "BusinessId" = ANY(@ids)"""),
        new(13, "VerificationCodes", """DELETE FROM "VerificationCodes" WHERE "BusinessId" = ANY(@ids)"""),
        new(14, "CustomerUpdateActions", """DELETE FROM "CustomerUpdateActions" WHERE "BusinessId" = ANY(@ids)"""),
        new(15, "FlaggedCustomerActivityLogs", """DELETE FROM "FlaggedCustomerActivityLogs" WHERE "BusinessId" = ANY(@ids)"""),
        new(16, "CustomerExtendedLoggingHistories", """DELETE FROM "CustomerExtendedLoggingHistories" WHERE "BusinessId" = ANY(@ids)"""),
        new(17, "ExternalIdentities", """DELETE FROM "ExternalIdentities" WHERE "BusinessId" = ANY(@ids)"""),
        new(18, "AiProviderInvocations", """DELETE FROM "AiProviderInvocations" WHERE "BusinessId" = ANY(@ids)"""),
        new(19, "AuditLogs", """DELETE FROM "AuditLogs" WHERE "BusinessId" = ANY(@ids)"""),
        new(20, "ScheduledReportRuns", """DELETE FROM "ScheduledReportRuns" WHERE "BusinessId" = ANY(@ids)"""),
        new(21, "BusinessSigningKeys", """DELETE FROM "BusinessSigningKeys" WHERE "BusinessId" = ANY(@ids)"""),
        new(22, "Conversations", """DELETE FROM "Conversations" WHERE "BusinessId" = ANY(@ids)"""),
        new(23, "Customers", """DELETE FROM "Customers" WHERE "BusinessId" = ANY(@ids)"""),
        new(24, "Widgets", """DELETE FROM "Widgets" WHERE "BusinessId" = ANY(@ids)"""),
        new(25, "Integrations", """DELETE FROM "Integrations" WHERE "BusinessId" = ANY(@ids)"""),
        new(26, "ConsumedSignedIdentityTokens", """DELETE FROM "ConsumedSignedIdentityTokens" WHERE "BusinessId" = ANY(@ids)"""),
        new(27, "SlaTargets", """DELETE FROM "SlaTargets" WHERE "BusinessId" = ANY(@ids)"""),
        new(28, "TagModeMappings", """DELETE FROM "TagModeMappings" WHERE "BusinessId" = ANY(@ids)"""),
        new(29, "AspNetUserClaims", """DELETE FROM "AspNetUserClaims" WHERE "UserId" IN (SELECT "Id" FROM "AspNetUsers" WHERE "BusinessId" = ANY(@ids))"""),
        new(30, "AspNetUserLogins", """DELETE FROM "AspNetUserLogins" WHERE "UserId" IN (SELECT "Id" FROM "AspNetUsers" WHERE "BusinessId" = ANY(@ids))"""),
        new(31, "AspNetUserTokens", """DELETE FROM "AspNetUserTokens" WHERE "UserId" IN (SELECT "Id" FROM "AspNetUsers" WHERE "BusinessId" = ANY(@ids))"""),
        new(32, "AspNetUserRoles", """DELETE FROM "AspNetUserRoles" WHERE "UserId" IN (SELECT "Id" FROM "AspNetUsers" WHERE "BusinessId" = ANY(@ids))"""),
        new(33, "AspNetUsers", """DELETE FROM "AspNetUsers" WHERE "BusinessId" = ANY(@ids)"""),
        new(34, "Businesses", """DELETE FROM "Businesses" WHERE "Id" = ANY(@ids)"""),
    ];

    internal static readonly ImmutableArray<string> UntouchedTables =
    [
        "AspNetRoles",
        "AspNetRoleClaims",
        "DataProtectionKeys",
    ];

    // Every FK of the EF model: (child table, column, principal table, delete behaviour).
    internal static readonly ImmutableArray<ModelFk> AccountedForeignKeys =
    [
        new("AspNetRoleClaims", "RoleId", "AspNetRoles", DeleteBehavior.Cascade),
        new("AspNetUserClaims", "UserId", "AspNetUsers", DeleteBehavior.Cascade),
        new("AspNetUserLogins", "UserId", "AspNetUsers", DeleteBehavior.Cascade),
        new("AspNetUserRoles", "RoleId", "AspNetRoles", DeleteBehavior.Cascade),
        new("AspNetUserRoles", "UserId", "AspNetUsers", DeleteBehavior.Cascade),
        new("AspNetUserTokens", "UserId", "AspNetUsers", DeleteBehavior.Cascade),
        new("AiProviderInvocations", "BusinessId", "Businesses", DeleteBehavior.Restrict),
        new("AiProviderInvocations", "ConversationId", "Conversations", DeleteBehavior.SetNull),
        new("AspNetUsers", "BusinessId", "Businesses", DeleteBehavior.Restrict),
        new("Approvals", "ApprovedBy", "AspNetUsers", DeleteBehavior.SetNull),
        new("Approvals", "BusinessId", "Businesses", DeleteBehavior.Restrict),
        new("Approvals", "ConversationId", "Conversations", DeleteBehavior.Cascade),
        new("Approvals", "CustomerId", "Customers", DeleteBehavior.Restrict),
        new("Approvals", "DeniedBy", "AspNetUsers", DeleteBehavior.SetNull),
        new("Approvals", "OrderCancellationId", "OrderCancellations", DeleteBehavior.Restrict),
        new("Approvals", "RefundId", "Refunds", DeleteBehavior.Restrict),
        new("AuditLogs", "BusinessId", "Businesses", DeleteBehavior.Restrict),
        new("AuditLogs", "ChangedByUserId", "AspNetUsers", DeleteBehavior.SetNull),
        new("AuditLogs", "ConversationId", "Conversations", DeleteBehavior.SetNull),
        new("BusinessSigningKeys", "BusinessId", "Businesses", DeleteBehavior.Restrict),
        new("BusinessSigningKeys", "CreatedByUserId", "AspNetUsers", DeleteBehavior.Restrict),
        new("ConsumedSignedIdentityTokens", "BusinessId", "Businesses", DeleteBehavior.Restrict),
        new("Conversations", "BusinessId", "Businesses", DeleteBehavior.Restrict),
        new("Conversations", "CustomerId", "Customers", DeleteBehavior.SetNull),
        new("Conversations", "WidgetId", "Widgets", DeleteBehavior.Restrict),
        new("ConversationHandoffs", "ActorId", "AspNetUsers", DeleteBehavior.SetNull),
        new("ConversationHandoffs", "BusinessId", "Businesses", DeleteBehavior.Restrict),
        new("ConversationHandoffs", "ConversationId", "Conversations", DeleteBehavior.SetNull),
        new("ConversationHandoffs", "TicketId", "Tickets", DeleteBehavior.SetNull),
        new("Customers", "BusinessId", "Businesses", DeleteBehavior.Restrict),
        new("CustomerExtendedLoggingHistories", "BusinessId", "Businesses", DeleteBehavior.Restrict),
        new("CustomerExtendedLoggingHistories", "CustomerId", "Customers", DeleteBehavior.Cascade),
        new("CustomerExtendedLoggingHistories", "DisabledByUserId", "AspNetUsers", DeleteBehavior.SetNull),
        new("CustomerExtendedLoggingHistories", "EnabledByUserId", "AspNetUsers", DeleteBehavior.Restrict),
        new("CustomerUpdateActions", "BusinessId", "Businesses", DeleteBehavior.Restrict),
        new("CustomerUpdateActions", "ConversationId", "Conversations", DeleteBehavior.SetNull),
        new("CustomerUpdateActions", "CustomerId", "Customers", DeleteBehavior.Restrict),
        new("ExternalIdentities", "BusinessId", "Businesses", DeleteBehavior.Restrict),
        new("ExternalIdentities", "CustomerId", "Customers", DeleteBehavior.Cascade),
        new("ExternalIdentities", "RevokedByUserId", "AspNetUsers", DeleteBehavior.SetNull),
        new("FlaggedCustomerActivityLogs", "BusinessId", "Businesses", DeleteBehavior.Restrict),
        new("FlaggedCustomerActivityLogs", "ConversationId", "Conversations", DeleteBehavior.SetNull),
        new("FlaggedCustomerActivityLogs", "CustomerId", "Customers", DeleteBehavior.Cascade),
        new("FlaggedCustomerActivityLogs", "PerformedByUserId", "AspNetUsers", DeleteBehavior.SetNull),
        new("Integrations", "BusinessId", "Businesses", DeleteBehavior.Restrict),
        new("KnowledgeChunks", "BusinessId", "Businesses", DeleteBehavior.Restrict),
        new("KnowledgeChunks", "KnowledgeDocumentId", "KnowledgeDocuments", DeleteBehavior.Cascade),
        new("KnowledgeDocuments", "BusinessId", "Businesses", DeleteBehavior.Restrict),
        new("Messages", "AuthorUserId", "AspNetUsers", DeleteBehavior.SetNull),
        new("Messages", "BusinessId", "Businesses", DeleteBehavior.Restrict),
        new("Messages", "ConversationId", "Conversations", DeleteBehavior.Cascade),
        new("MessageKnowledgeSources", "BusinessId", "Businesses", DeleteBehavior.Restrict),
        new("MessageKnowledgeSources", "KnowledgeChunkId", "KnowledgeChunks", DeleteBehavior.SetNull),
        new("MessageKnowledgeSources", "KnowledgeDocumentId", "KnowledgeDocuments", DeleteBehavior.SetNull),
        new("MessageKnowledgeSources", "MessageId", "Messages", DeleteBehavior.Cascade),
        new("Notifications", "ActorUserId", "AspNetUsers", DeleteBehavior.SetNull),
        new("Notifications", "BusinessId", "Businesses", DeleteBehavior.Restrict),
        new("Notifications", "ConversationId", "Conversations", DeleteBehavior.SetNull),
        new("Notifications", "TicketId", "Tickets", DeleteBehavior.SetNull),
        new("NotificationRecipients", "BusinessId", "Businesses", DeleteBehavior.Restrict),
        new("NotificationRecipients", "NotificationId", "Notifications", DeleteBehavior.Cascade),
        new("NotificationRecipients", "UserId", "AspNetUsers", DeleteBehavior.Restrict),
        new("OrderCancellations", "BusinessId", "Businesses", DeleteBehavior.Restrict),
        new("OrderCancellations", "ConversationId", "Conversations", DeleteBehavior.SetNull),
        new("OrderCancellations", "CustomerId", "Customers", DeleteBehavior.Restrict),
        new("Refunds", "BusinessId", "Businesses", DeleteBehavior.Restrict),
        new("Refunds", "ConversationId", "Conversations", DeleteBehavior.SetNull),
        new("Refunds", "CustomerId", "Customers", DeleteBehavior.Restrict),
        new("RefundItems", "BusinessId", "Businesses", DeleteBehavior.Restrict),
        new("RefundItems", "RefundId", "Refunds", DeleteBehavior.Cascade),
        new("ScheduledReportRuns", "BusinessId", "Businesses", DeleteBehavior.Restrict),
        new("ScheduledReportRuns", "RecipientOwnerId", "AspNetUsers", DeleteBehavior.SetNull),
        new("SlaTargets", "BusinessId", "Businesses", DeleteBehavior.Restrict),
        new("TagModeMappings", "BusinessId", "Businesses", DeleteBehavior.Restrict),
        new("Tickets", "BusinessId", "Businesses", DeleteBehavior.Restrict),
        new("Tickets", "ClaimedByUserId", "AspNetUsers", DeleteBehavior.SetNull),
        new("Tickets", "ConversationId", "Conversations", DeleteBehavior.SetNull),
        new("Tickets", "CustomerId", "Customers", DeleteBehavior.Restrict),
        new("Tickets", "ResolvedByUserId", "AspNetUsers", DeleteBehavior.SetNull),
        new("VerificationCodes", "BusinessId", "Businesses", DeleteBehavior.Restrict),
        new("VerificationCodes", "ConversationId", "Conversations", DeleteBehavior.Cascade),
        new("VerificationCodes", "CustomerId", "Customers", DeleteBehavior.Cascade),
        new("Widgets", "BusinessId", "Businesses", DeleteBehavior.Restrict),
    ];

    // The CASCADE / SET NULL FKs whose child carries its own BusinessId (principal not AspNetRoles).
    internal static readonly ImmutableArray<ModelFk> CrossOwnerChecks =
    [
        new("Approvals", "ConversationId", "Conversations", DeleteBehavior.Cascade),
        new("CustomerExtendedLoggingHistories", "CustomerId", "Customers", DeleteBehavior.Cascade),
        new("ExternalIdentities", "CustomerId", "Customers", DeleteBehavior.Cascade),
        new("FlaggedCustomerActivityLogs", "CustomerId", "Customers", DeleteBehavior.Cascade),
        new("KnowledgeChunks", "KnowledgeDocumentId", "KnowledgeDocuments", DeleteBehavior.Cascade),
        new("MessageKnowledgeSources", "MessageId", "Messages", DeleteBehavior.Cascade),
        new("Messages", "ConversationId", "Conversations", DeleteBehavior.Cascade),
        new("NotificationRecipients", "NotificationId", "Notifications", DeleteBehavior.Cascade),
        new("RefundItems", "RefundId", "Refunds", DeleteBehavior.Cascade),
        new("VerificationCodes", "ConversationId", "Conversations", DeleteBehavior.Cascade),
        new("VerificationCodes", "CustomerId", "Customers", DeleteBehavior.Cascade),
        new("AiProviderInvocations", "ConversationId", "Conversations", DeleteBehavior.SetNull),
        new("Approvals", "ApprovedBy", "AspNetUsers", DeleteBehavior.SetNull),
        new("Approvals", "DeniedBy", "AspNetUsers", DeleteBehavior.SetNull),
        new("AuditLogs", "ChangedByUserId", "AspNetUsers", DeleteBehavior.SetNull),
        new("AuditLogs", "ConversationId", "Conversations", DeleteBehavior.SetNull),
        new("ConversationHandoffs", "ActorId", "AspNetUsers", DeleteBehavior.SetNull),
        new("ConversationHandoffs", "ConversationId", "Conversations", DeleteBehavior.SetNull),
        new("ConversationHandoffs", "TicketId", "Tickets", DeleteBehavior.SetNull),
        new("Conversations", "CustomerId", "Customers", DeleteBehavior.SetNull),
        new("CustomerExtendedLoggingHistories", "DisabledByUserId", "AspNetUsers", DeleteBehavior.SetNull),
        new("CustomerUpdateActions", "ConversationId", "Conversations", DeleteBehavior.SetNull),
        new("ExternalIdentities", "RevokedByUserId", "AspNetUsers", DeleteBehavior.SetNull),
        new("FlaggedCustomerActivityLogs", "ConversationId", "Conversations", DeleteBehavior.SetNull),
        new("FlaggedCustomerActivityLogs", "PerformedByUserId", "AspNetUsers", DeleteBehavior.SetNull),
        new("MessageKnowledgeSources", "KnowledgeChunkId", "KnowledgeChunks", DeleteBehavior.SetNull),
        new("MessageKnowledgeSources", "KnowledgeDocumentId", "KnowledgeDocuments", DeleteBehavior.SetNull),
        new("Messages", "AuthorUserId", "AspNetUsers", DeleteBehavior.SetNull),
        new("Notifications", "ActorUserId", "AspNetUsers", DeleteBehavior.SetNull),
        new("Notifications", "ConversationId", "Conversations", DeleteBehavior.SetNull),
        new("Notifications", "TicketId", "Tickets", DeleteBehavior.SetNull),
        new("OrderCancellations", "ConversationId", "Conversations", DeleteBehavior.SetNull),
        new("Refunds", "ConversationId", "Conversations", DeleteBehavior.SetNull),
        new("ScheduledReportRuns", "RecipientOwnerId", "AspNetUsers", DeleteBehavior.SetNull),
        new("Tickets", "ClaimedByUserId", "AspNetUsers", DeleteBehavior.SetNull),
        new("Tickets", "ConversationId", "Conversations", DeleteBehavior.SetNull),
        new("Tickets", "ResolvedByUserId", "AspNetUsers", DeleteBehavior.SetNull),
    ];

    private const string LockSql =
        """SELECT "Id" FROM "Businesses" WHERE "Id" = ANY(@ids) ORDER BY "Id" FOR UPDATE""";

    private const string CreationTimeSql =
        """SELECT count(*) AS created_before FROM "Businesses" WHERE "Id" = ANY(@ids) AND "CreatedAt" < @processStartUtc""";

    private static readonly DateTimeOffset ProcessStartUtc =
        new DateTimeOffset(Process.GetCurrentProcess().StartTime).ToUniversalTime();

    private static readonly Lazy<string> ConnectionString = new(BuildConnectionString);

    // Pure. Throws ArgumentException on: empty input, Guid.Empty, any owner Id. De-duplicates.
    internal static Guid[] ValidateIds(IEnumerable<Guid> businessIds)
    {
        ArgumentNullException.ThrowIfNull(businessIds);
        var ids = businessIds.ToArray();

        if (ids.Length == 0)
        {
            throw new ArgumentException("No Business Id was given; the cleanup refuses an empty list.", nameof(businessIds));
        }

        if (ids.Contains(Guid.Empty))
        {
            throw new ArgumentException("Guid.Empty is not a Business Id a test created; the cleanup refuses it.", nameof(businessIds));
        }

        var owners = ids.Where(OwnerBusinessIds.Contains).Distinct().ToArray();
        if (owners.Length > 0)
        {
            throw new ArgumentException(
                $"The cleanup refuses the protected owner Business Id(s): {string.Join(", ", owners)}.", nameof(businessIds));
        }

        return ids.Distinct().ToArray();
    }

    // The delete. Validates, then runs one transaction (two batches and a commit).
    internal static Task DeleteOwnedRowsAsync(IEnumerable<Guid> businessIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(businessIds);
        return DeleteOwnedRowsAsync(businessIds.ToArray(), null, null, null, ct);
    }

    // DisposeAsync convenience. Empty businessIds: opens no connection, sends no SQL, refuses nothing,
    // and disposes `resource` (may be null). Non-empty: calls DeleteOwnedRowsAsync (every refusal), then
    // disposes `resource`. Both are always attempted; one failure is rethrown as is, two as AggregateException.
    internal static async Task DeleteThenDisposeAsync(IReadOnlyCollection<Guid> businessIds, IAsyncDisposable? resource)
    {
        if (businessIds.Count == 0)
        {
            if (resource is not null)
            {
                await resource.DisposeAsync();
            }
            return;
        }

        try
        {
            await DeleteOwnedRowsAsync(businessIds);
        }
        catch (Exception deleteException)
        {
            if (resource is not null)
            {
                try
                {
                    await resource.DisposeAsync();
                }
                catch (Exception disposeException)
                {
                    throw new AggregateException(deleteException, disposeException);
                }
            }
            throw;
        }

        if (resource is not null)
        {
            await resource.DisposeAsync();
        }
    }

    // Test seam, used only by the helper's own tests. Runs every refusal of the overload above;
    // no parameter skips one. omitSteps: step numbers of Statements to leave out, 1-34 only.
    // afterLock: invoked after round trip 1 while the Business rows are locked. referenceTime:
    // replaces the process start time in the creation-time check; must be later than it.
    internal static async Task DeleteOwnedRowsAsync(
        Guid[] ids,
        IReadOnlySet<int>? omitSteps,
        Func<Task>? afterLock,
        DateTimeOffset? referenceTime,
        CancellationToken ct)
    {
        // E-1: copy the inputs before any validation; everything below uses the copies.
        ArgumentNullException.ThrowIfNull(ids);
        var idCopy = ImmutableArray.Create(ids);
        var omitCopy = omitSteps is null ? FrozenSet<int>.Empty : omitSteps.ToFrozenSet();

        var validIds = ValidateIds(idCopy);

        var outOfRange = omitCopy.Where(step => step is < 1 or > 34).Order().ToArray();
        if (outOfRange.Length > 0)
        {
            throw new ArgumentException(
                $"omitSteps accepts step numbers 1-34 only; got {string.Join(", ", outOfRange)}.", nameof(omitSteps));
        }

        var statements = Statements.Where(s => !omitCopy.Contains(s.Step)).ToImmutableArray();
        if (statements.Length == 0)
        {
            throw new ArgumentException("omitSteps removes every statement; nothing would be deleted.", nameof(omitSteps));
        }

        if (referenceTime is { } reference && reference <= ProcessStartUtc)
        {
            throw new ArgumentException(
                $"referenceTime {reference:O} is not later than the process start {ProcessStartUtc:O}.", nameof(referenceTime));
        }

        var creationCutoff = (referenceTime ?? ProcessStartUtc).ToUniversalTime();
        var idList = string.Join(", ", validIds);

        // From here on every failure is a TestCleanupException (E-2).
        var stage = "step 2 (connect and begin)";
        NpgsqlBatch? roundTrip1 = null;
        NpgsqlBatch? roundTrip2 = null;

        try
        {
            await using var connection = new NpgsqlConnection(ConnectionString.Value);
            await connection.OpenAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

            // Round trip 1: steps 2-5.
            stage = "round trip 1";
            roundTrip1 = new NpgsqlBatch(connection, transaction);
            roundTrip1.BatchCommands.Add(new NpgsqlBatchCommand("SET LOCAL lock_timeout = '15s'"));
            roundTrip1.BatchCommands.Add(new NpgsqlBatchCommand("SET LOCAL statement_timeout = '60s'"));
            roundTrip1.BatchCommands.Add(WithIds(LockSql, validIds));
            var creationCommand = WithIds(CreationTimeSql, validIds);
            creationCommand.Parameters.Add(new NpgsqlParameter("processStartUtc", NpgsqlDbType.TimestampTz) { Value = creationCutoff });
            roundTrip1.BatchCommands.Add(creationCommand);
            roundTrip1.BatchCommands.Add(WithIds(CrossOwnerSql, validIds));

            long? createdBefore = null;
            var crossOwnerCounts = new long?[CrossOwnerChecks.Length];
            await using (var reader = await roundTrip1.ExecuteReaderAsync(ct))
            {
                do
                {
                    if (reader.FieldCount == 0)
                    {
                        continue;
                    }

                    switch (reader.GetName(0))
                    {
                        case "Id":
                            while (await reader.ReadAsync(ct)) { }
                            break;
                        case "created_before":
                            await reader.ReadAsync(ct);
                            createdBefore = reader.GetInt64(0);
                            break;
                        case "check_index":
                            while (await reader.ReadAsync(ct))
                            {
                                crossOwnerCounts[reader.GetInt32(0)] = reader.GetInt64(1);
                            }
                            break;
                    }
                }
                while (await reader.NextResultAsync(ct));
            }

            if (createdBefore is null || crossOwnerCounts.Any(c => c is null))
            {
                throw new TestCleanupException(
                    $"TestBusinessCleanup: round trip 1 did not return every check result. Business Ids: {idList}.", null);
            }

            if (createdBefore > 0)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw new TestCleanupException(
                    $"TestBusinessCleanup refused at step 4 (creation time): {createdBefore} listed Business(es) were created before " +
                    $"{creationCutoff:O}. Nothing was deleted. Business Ids: {idList}.", null);
            }

            var crossOwner = CrossOwnerChecks
                .Select((fk, i) => (fk, count: crossOwnerCounts[i]!.Value))
                .Where(x => x.count > 0)
                .Select(x => $"{x.fk.ChildTable}.{x.fk.Column} -> {x.fk.PrincipalTable} ({x.fk.OnDelete}): {x.count}")
                .ToArray();
            if (crossOwner.Length > 0)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw new TestCleanupException(
                    $"TestBusinessCleanup refused at step 5 (cross-owner check): rows of other Businesses reference rows of a listed " +
                    $"Business through {string.Join("; ", crossOwner)}. Nothing was deleted. Business Ids: {idList}.", null);
            }

            if (afterLock is not null)
            {
                stage = "afterLock callback";
                await afterLock();
            }

            // Round trip 2: step 6, the remaining statements in order.
            stage = "round trip 2";
            roundTrip2 = new NpgsqlBatch(connection, transaction);
            foreach (var statement in statements)
            {
                roundTrip2.BatchCommands.Add(WithIds(statement.Sql, validIds));
            }
            await roundTrip2.ExecuteNonQueryAsync(ct);

            // Step 7.
            stage = "step 7 (commit)";
            await transaction.CommitAsync(ct);
        }
        catch (TestCleanupException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new TestCleanupException(Describe(exception, stage, roundTrip1, roundTrip2, statements, idList), exception);
        }
        finally
        {
            if (roundTrip1 is not null) await roundTrip1.DisposeAsync();
            if (roundTrip2 is not null) await roundTrip2.DisposeAsync();
        }
    }

    // One SELECT with one count per cross-owner FK: rows whose own BusinessId is not listed and
    // whose referenced principal row belongs to a listed Business.
    private static readonly string CrossOwnerSql = string.Join(
        " UNION ALL ",
        CrossOwnerChecks.Select((fk, i) =>
            $"""SELECT {i} AS check_index, count(*) AS n FROM "{fk.ChildTable}" c JOIN "{fk.PrincipalTable}" p ON p."Id" = c."{fk.Column}" WHERE p."BusinessId" = ANY(@ids) AND NOT (c."BusinessId" = ANY(@ids))"""));

    private static NpgsqlBatchCommand WithIds(string sql, Guid[] ids)
    {
        var command = new NpgsqlBatchCommand(sql);
        command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids });
        return command;
    }

    private static string Describe(
        Exception exception,
        string stage,
        NpgsqlBatch? roundTrip1,
        NpgsqlBatch? roundTrip2,
        ImmutableArray<CleanupStatement> statements,
        string idList)
    {
        var sqlState = exception is PostgresException pg ? pg.SqlState : "(none)";
        var failed = (exception as NpgsqlException)?.BatchCommand;

        string where;
        if (stage == "round trip 2" && roundTrip2 is not null)
        {
            var index = failed is null ? -1 : roundTrip2.BatchCommands.IndexOf(failed);
            where = index >= 0
                ? $"step {statements[index].Step} ({statements[index].Table}), statement: {statements[index].Sql}"
                : "round trip 2; the failing statement could not be identified";
        }
        else if (stage == "round trip 1" && roundTrip1 is not null)
        {
            var index = failed is null ? -1 : roundTrip1.BatchCommands.IndexOf(failed);
            where = index switch
            {
                0 or 1 => "step 2 (SET LOCAL)",
                2 => "step 3 (Businesses row lock)",
                3 => "step 4 (creation-time check)",
                4 => "step 5 (cross-owner check)",
                _ => "round trip 1; the failing command could not be identified",
            };
        }
        else
        {
            where = stage;
        }

        return $"TestBusinessCleanup failed at {where}: SQLSTATE {sqlState}: {exception.Message} " +
               $"The transaction was rolled back. Business Ids: {idList}.";
    }

    private static string BuildConnectionString()
    {
        var built = new NpgsqlConnectionStringBuilder(TestDatabase.ConnectionString)
        {
            IncludeFailedBatchedCommand = true,
        }.ConnectionString;
        TestDatabase.RequireName(built);
        return built;
    }
}

internal sealed record CleanupStatement(int Step, string Table, string Sql);

internal sealed record ModelFk(string ChildTable, string Column, string PrincipalTable, DeleteBehavior OnDelete);

internal sealed class TestCleanupException(string message, Exception? inner) : Exception(message, inner);
