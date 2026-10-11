using backend.Auth;
using backend.Data;
using backend.Enums;
using backend.Interfaces;
using backend.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pgvector;

namespace backend.Tests;

/// <summary>
/// Tests for TestBusinessCleanup (design section 8, H1-H7 and H9; errata E-9
/// wrapper cases). Real Postgres. Every Business created here is handed to
/// the helper in DisposeAsync, like any adopted class. In the KnowledgeChunks
/// collection because H4 writes a KnowledgeChunks row (errata-2 E-13).
/// </summary>
[Collection("KnowledgeChunks collection")]
public class TestBusinessCleanupTests : IAsyncLifetime
{
    private static readonly TimeSpan BlockingDeadline = TimeSpan.FromSeconds(10);

    // The 29 tables with BusinessId -> Businesses, and the 4 Identity child tables; listed
    // here independently of the helper's statement list.
    private static readonly string[] BusinessIdTables =
    [
        "MessageKnowledgeSources", "KnowledgeChunks", "KnowledgeDocuments", "Messages", "Approvals",
        "RefundItems", "Refunds", "OrderCancellations", "NotificationRecipients", "Notifications",
        "ConversationHandoffs", "Tickets", "VerificationCodes", "CustomerUpdateActions",
        "FlaggedCustomerActivityLogs", "CustomerExtendedLoggingHistories", "ExternalIdentities",
        "AiProviderInvocations", "AuditLogs", "ScheduledReportRuns", "BusinessSigningKeys",
        "Conversations", "Customers", "Widgets", "Integrations", "ConsumedSignedIdentityTokens",
        "SlaTargets", "TagModeMappings", "AspNetUsers",
    ];

    private static readonly string[] IdentityChildTables =
        ["AspNetUserClaims", "AspNetUserLogins", "AspNetUserTokens", "AspNetUserRoles"];

    private ServiceProvider _provider = null!;
    private string _connectionString = null!;
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync()
    {
        _connectionString = TestDatabase.ConnectionString;
        _provider = new ServiceCollection()
            .AddSingleton<ICurrentTenantAccessor>(new FixedTenantAccessor(null))
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(_connectionString, x => x.UseVector()))
            .BuildServiceProvider();

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await TestBusinessCleanup.DeleteThenDisposeAsync(_businessIdsToClean, _provider);
    }

    // ── H1-H3: pure refusals ──

    [Fact]
    public void ValidateIds_Empty_Throws()
    {
        Assert.Throws<ArgumentException>(() => TestBusinessCleanup.ValidateIds([]));
    }

    public static TheoryData<string, bool> OwnerIdCases => new()
    {
        { "b2636ee3-0e5a-4c84-af4a-725ef9b659cd", false },
        { "b2636ee3-0e5a-4c84-af4a-725ef9b659cd", true },
        { "28e0c251-d4a4-49f3-9e2a-4905bb23a7cf", false },
        { "28e0c251-d4a4-49f3-9e2a-4905bb23a7cf", true },
        { "50903e70-54bd-471a-ba7c-bd2344c6a5c3", false },
        { "50903e70-54bd-471a-ba7c-bd2344c6a5c3", true },
    };

    [Theory]
    [MemberData(nameof(OwnerIdCases))]
    public void ValidateIds_OwnerId_Throws(string ownerId, bool mixedWithFreshId)
    {
        Guid[] ids = mixedWithFreshId ? [Guid.NewGuid(), Guid.Parse(ownerId)] : [Guid.Parse(ownerId)];

        Assert.Throws<ArgumentException>(() => TestBusinessCleanup.ValidateIds(ids));
    }

    [Fact]
    public void ValidateIds_GuidEmpty_Throws()
    {
        Assert.Throws<ArgumentException>(() => TestBusinessCleanup.ValidateIds([Guid.Empty]));
    }

    // ── H4: every owned row, every table; step-4 refusal ──

    [Fact]
    public async Task Delete_RemovesEveryOwnedRow_InAllTables()
    {
        var businessId = await SeedEveryTableAsync();

        var seeded = await CountOwnedRowsAsync(businessId);
        var empty = seeded.Where(kv => kv.Value == 0).Select(kv => kv.Key).ToList();
        Assert.True(empty.Count == 0, $"Seeding left tables without a row: {string.Join(", ", empty)}");

        var referenceTime = DateTimeOffset.UtcNow.AddSeconds(1);
        var refusal = await Assert.ThrowsAsync<TestCleanupException>(() =>
            TestBusinessCleanup.DeleteOwnedRowsAsync([businessId], null, null, referenceTime, CancellationToken.None));
        Assert.Contains("step 4", refusal.Message);
        Assert.Equal(seeded, await CountOwnedRowsAsync(businessId));

        await TestBusinessCleanup.DeleteOwnedRowsAsync([businessId]);

        var left = (await CountOwnedRowsAsync(businessId)).Where(kv => kv.Value != 0).Select(kv => $"{kv.Key}={kv.Value}").ToList();
        Assert.True(left.Count == 0, $"Rows left after cleanup: {string.Join(", ", left)}");
    }

    // ── H5: rows already gone, unknown Id ──

    [Fact]
    public async Task Delete_TwiceAndUnknownId_Succeeds()
    {
        var businessId = await CreateBusinessAsync();

        await TestBusinessCleanup.DeleteOwnedRowsAsync([businessId]);
        await TestBusinessCleanup.DeleteOwnedRowsAsync([businessId]);
        Assert.Equal(0, await CountAsync("""SELECT count(*) FROM "Businesses" WHERE "Id" = @id""", businessId));

        await TestBusinessCleanup.DeleteOwnedRowsAsync([Guid.NewGuid()]);
    }

    // ── H6: cross-owner reference ──

    [Fact]
    public async Task Delete_CrossOwnerReference_RefusesAndDeletesNothing()
    {
        var businessA = await CreateBusinessAsync();
        var businessB = await CreateBusinessAsync();
        var now = DateTimeOffset.UtcNow;

        Guid conversationA, customerB, messageB, ticketB;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var widgetA = NewWidget(businessA, now);
            var conversation = NewConversation(businessA, widgetA.Id, null, now);
            var customer = NewCustomer(businessB, now);
            var message = new Message
            {
                Id = Guid.NewGuid(), BusinessId = businessB, ConversationId = conversation.Id,
                Role = MessageRole.Customer, Content = "cross-owner message", CreatedAt = now,
            };
            var ticket = NewTicket(businessB, customer.Id, conversation.Id, now);
            db.AddRange(widgetA, conversation, customer, message, ticket);
            await db.SaveChangesAsync();
            (conversationA, customerB, messageB, ticketB) = (conversation.Id, customer.Id, message.Id, ticket.Id);
        }

        var refusal = await Assert.ThrowsAsync<TestCleanupException>(() => TestBusinessCleanup.DeleteOwnedRowsAsync([businessA]));
        Assert.Contains("cross-owner", refusal.Message);

        Assert.Equal(1, await CountAsync("""SELECT count(*) FROM "Businesses" WHERE "Id" = @id""", businessA));
        Assert.Equal(1, await CountAsync("""SELECT count(*) FROM "Businesses" WHERE "Id" = @id""", businessB));
        Assert.Equal(1, await CountAsync("""SELECT count(*) FROM "Widgets" WHERE "BusinessId" = @id""", businessA));
        Assert.Equal(1, await CountAsync("""SELECT count(*) FROM "Conversations" WHERE "Id" = @id""", conversationA));
        Assert.Equal(1, await CountAsync("""SELECT count(*) FROM "Customers" WHERE "Id" = @id""", customerB));
        Assert.Equal(1, await CountAsync($"""SELECT count(*) FROM "Messages" WHERE "Id" = @id AND "ConversationId" = '{conversationA}'""", messageB));
        Assert.Equal(1, await CountAsync($"""SELECT count(*) FROM "Tickets" WHERE "Id" = @id AND "ConversationId" = '{conversationA}'""", ticketB));

        await TestBusinessCleanup.DeleteOwnedRowsAsync([businessA, businessB]);

        Assert.Equal(0, await CountAsync("""SELECT count(*) FROM "Businesses" WHERE "Id" = @id""", businessA));
        Assert.Equal(0, await CountAsync("""SELECT count(*) FROM "Businesses" WHERE "Id" = @id""", businessB));
    }

    // ── H7: concurrent writer serialized by the Businesses row lock ──

    [Fact]
    public async Task Delete_ConcurrentWriter_IsSerializedByBusinessLock()
    {
        // (a) A writer's open transaction has inserted an AuditLogs row for A; the helper must block
        // until the writer commits, then delete the row.
        var businessA = await CreateBusinessAsync();
        await using (var writer = await OpenAsync())
        {
            await using var writerTx = await writer.BeginTransactionAsync();
            var writerPid = await BackendPidAsync(writer, writerTx);
            await InsertAuditLogAsync(writer, writerTx, businessA);

            var helper = TestBusinessCleanup.DeleteOwnedRowsAsync([businessA]);

            Assert.True(
                await WaitUntilAsync(BlockingDeadline, () => AnyBackendBlockedByAsync(writerPid)),
                "H7(a): no backend was observed waiting on a lock held by the writer.");
            Assert.False(helper.IsCompleted, "H7(a): the helper completed while the writer held its transaction open.");

            await writerTx.CommitAsync();
            await helper;
        }
        Assert.Equal(0, await CountAsync("""SELECT count(*) FROM "AuditLogs" WHERE "BusinessId" = @id""", businessA));
        Assert.Equal(0, await CountAsync("""SELECT count(*) FROM "Businesses" WHERE "Id" = @id""", businessA));

        // (b) Through afterLock: a writer starts an AuditLogs insert for A2 while the helper holds the
        // row lock; the insert must wait, and fail with 23503 once the helper has committed.
        var businessA2 = await CreateBusinessAsync();
        await using var lateWriter = await OpenAsync();
        await using var lateWriterTx = await lateWriter.BeginTransactionAsync();
        var lateWriterPid = await BackendPidAsync(lateWriter, lateWriterTx);
        Task? insert = null;

        await TestBusinessCleanup.DeleteOwnedRowsAsync([businessA2], null, async () =>
        {
            insert = InsertAuditLogAsync(lateWriter, lateWriterTx, businessA2);
            Assert.True(
                await WaitUntilAsync(BlockingDeadline, () => IsWaitingOnLockAsync(lateWriterPid)),
                "H7(b): the writer's insert was not observed waiting on a lock.");
        }, null, CancellationToken.None);

        Assert.NotNull(insert);
        var failure = await Assert.ThrowsAsync<PostgresException>(() => insert!);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, failure.SqlState);
        await lateWriterTx.RollbackAsync();

        Assert.Equal(0, await CountAsync("""SELECT count(*) FROM "AuditLogs" WHERE "BusinessId" = @id""", businessA2));
        Assert.Equal(0, await CountAsync("""SELECT count(*) FROM "Businesses" WHERE "Id" = @id""", businessA2));
    }

    // ── H9: a failing batch statement is named by step and table ──

    [Fact]
    public async Task Delete_FailingBatchStatement_IsNamedByStepAndTable()
    {
        var businessId = await CreateBusinessAsync();
        var now = DateTimeOffset.UtcNow;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var customer = NewCustomer(businessId, now);
            db.AddRange(customer, NewTicket(businessId, customer.Id, null, now));
            await db.SaveChangesAsync();
        }

        var failure = await Assert.ThrowsAsync<TestCleanupException>(() =>
            TestBusinessCleanup.DeleteOwnedRowsAsync([businessId], new HashSet<int> { 12 }, null, null, CancellationToken.None));

        Assert.Contains("step 23 (Customers)", failure.Message);
        var inner = Assert.IsType<PostgresException>(failure.InnerException);
        Assert.Equal(PostgresErrorCodes.RestrictViolation, inner.SqlState);
        Assert.Equal(1, await CountAsync("""SELECT count(*) FROM "Businesses" WHERE "Id" = @id""", businessId));
        Assert.Equal(1, await CountAsync("""SELECT count(*) FROM "Customers" WHERE "BusinessId" = @id""", businessId));
        Assert.Equal(1, await CountAsync("""SELECT count(*) FROM "Tickets" WHERE "BusinessId" = @id""", businessId));
    }

    // ── E-9: DeleteThenDisposeAsync ──

    [Fact]
    public async Task DeleteThenDispose_EmptyList_DisposesAndDeletesNothing()
    {
        var businessId = await CreateBusinessAsync();
        var resource = new RecordingDisposable();

        await TestBusinessCleanup.DeleteThenDisposeAsync([], resource);

        Assert.True(resource.Disposed);
        Assert.Equal(1, await CountAsync("""SELECT count(*) FROM "Businesses" WHERE "Id" = @id""", businessId));
    }

    [Fact]
    public async Task DeleteThenDispose_NonEmptyList_DeletesRowsAndDisposes()
    {
        var businessId = await CreateBusinessAsync();
        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Add(NewWidget(businessId, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }
        var resource = new RecordingDisposable();

        await TestBusinessCleanup.DeleteThenDisposeAsync([businessId], resource);

        Assert.True(resource.Disposed);
        Assert.Equal(0, await CountAsync("""SELECT count(*) FROM "Widgets" WHERE "BusinessId" = @id""", businessId));
        Assert.Equal(0, await CountAsync("""SELECT count(*) FROM "Businesses" WHERE "Id" = @id""", businessId));
    }

    [Fact]
    public async Task DeleteThenDispose_DeleteThrows_DisposesAndRethrows()
    {
        var resource = new RecordingDisposable();

        var failure = await Assert.ThrowsAsync<ArgumentException>(() =>
            TestBusinessCleanup.DeleteThenDisposeAsync([Guid.Empty], resource));

        Assert.Contains("Guid.Empty", failure.Message);
        Assert.True(resource.Disposed);
    }

    [Fact]
    public async Task DeleteThenDispose_DeleteAndDisposeThrow_ThrowsAggregateOfBoth()
    {
        var disposeFailure = new InvalidOperationException("dispose failed");
        var resource = new RecordingDisposable(disposeFailure);

        var failure = await Assert.ThrowsAsync<AggregateException>(() =>
            TestBusinessCleanup.DeleteThenDisposeAsync([Guid.Empty], resource));

        Assert.True(resource.Disposed);
        Assert.Equal(2, failure.InnerExceptions.Count);
        Assert.IsType<ArgumentException>(failure.InnerExceptions[0]);
        Assert.Same(disposeFailure, failure.InnerExceptions[1]);
    }

    // ── Seeding ──

    private async Task<Guid> CreateBusinessAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var business = new Business
        {
            Id = Guid.NewGuid(),
            Name = $"Cleanup Helper Test {Guid.NewGuid():N}",
            TimeZone = "Europe/Copenhagen",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Businesses.Add(business);
        await db.SaveChangesAsync();
        _businessIdsToClean.Add(business.Id);
        return business.Id;
    }

    // One row in each of the 29 BusinessId tables and each of the 4 Identity child tables.
    private async Task<Guid> SeedEveryTableAsync()
    {
        var businessId = await CreateBusinessAsync();
        var now = DateTimeOffset.UtcNow;

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var ownerRoleName = RoleNames.Owner.ToUpperInvariant();
        var ownerRoleId = await db.Roles.Where(r => r.NormalizedName == ownerRoleName).Select(r => (Guid?)r.Id).SingleOrDefaultAsync();
        Assert.True(ownerRoleId.HasValue, $"The Owner role (normalized name {ownerRoleName}) is missing from AspNetRoles; H4 does not create roles.");

        var userName = $"cleanup-helper-{Guid.NewGuid():N}@example.test";
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(), BusinessId = businessId, UserName = userName, NormalizedUserName = userName.ToUpperInvariant(),
            Email = userName, NormalizedEmail = userName.ToUpperInvariant(), SecurityStamp = Guid.NewGuid().ToString(),
            ConcurrencyStamp = Guid.NewGuid().ToString(), CreatedAt = now,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        db.UserClaims.Add(new IdentityUserClaim<Guid> { UserId = user.Id, ClaimType = "cleanup-test", ClaimValue = "1" });
        db.UserLogins.Add(new IdentityUserLogin<Guid> { UserId = user.Id, LoginProvider = "cleanup-test", ProviderKey = Guid.NewGuid().ToString(), ProviderDisplayName = "cleanup-test" });
        db.UserTokens.Add(new IdentityUserToken<Guid> { UserId = user.Id, LoginProvider = "cleanup-test", Name = "token", Value = "1" });
        db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = user.Id, RoleId = ownerRoleId!.Value });

        var widget = NewWidget(businessId, now);
        var customer = NewCustomer(businessId, now);
        var conversation = NewConversation(businessId, widget.Id, customer.Id, now);
        var document = new KnowledgeDocument
        {
            Id = Guid.NewGuid(), BusinessId = businessId, Type = KnowledgeDocumentType.Faq, Title = "cleanup", Content = "cleanup",
            CreatedAt = now, UpdatedAt = now,
        };
        db.AddRange(widget, customer, conversation, document);
        await db.SaveChangesAsync();

        var chunk = new KnowledgeChunk
        {
            Id = Guid.NewGuid(), BusinessId = businessId, KnowledgeDocumentId = document.Id, ChunkIndex = 0, Content = "cleanup",
            Embedding = new Vector(new float[1024]), EmbeddingModel = "cleanup-test", CreatedAt = now, UpdatedAt = now,
        };
        var message = new Message
        {
            Id = Guid.NewGuid(), BusinessId = businessId, ConversationId = conversation.Id, Role = MessageRole.Customer,
            Content = "cleanup", CreatedAt = now,
        };
        var refund = new Refund
        {
            Id = Guid.NewGuid(), BusinessId = businessId, CustomerId = customer.Id, ConversationId = conversation.Id,
            ExternalOrderId = "cleanup-order", Method = RefundMethod.OriginalPaymentMethod, IdempotencyKey = Guid.NewGuid().ToString(),
            Currency = "DKK", RequestedAt = now, UpdatedAt = now,
        };
        var cancellation = new OrderCancellation
        {
            Id = Guid.NewGuid(), BusinessId = businessId, CustomerId = customer.Id, ConversationId = conversation.Id,
            ExternalOrderId = "cleanup-order", IdempotencyKey = Guid.NewGuid().ToString(), RequestedAt = now, UpdatedAt = now,
        };
        var notification = new Notification
        {
            Id = Guid.NewGuid(), BusinessId = businessId, Category = NotificationCategory.System, Type = "KnowledgeImportCompleted",
            Title = "cleanup", ActorType = ActorType.BusinessInternalSystem, CreatedAt = now,
        };
        var ticket = NewTicket(businessId, customer.Id, conversation.Id, now);
        db.AddRange(chunk, message, refund, cancellation, notification, ticket);
        await db.SaveChangesAsync();

        db.AddRange(
            new MessageKnowledgeSource
            {
                Id = Guid.NewGuid(), BusinessId = businessId, MessageId = message.Id, KnowledgeDocumentId = document.Id,
                KnowledgeChunkId = chunk.Id, DocumentTitleSnapshot = "cleanup", ChunkContentSnapshot = "cleanup", CreatedAt = now,
            },
            new Approval
            {
                Id = Guid.NewGuid(), BusinessId = businessId, ConversationId = conversation.Id, CustomerId = customer.Id,
                ToolName = "cleanup", ResourceId = "cleanup", CanonicalArguments = "{}", ActionHash = Guid.NewGuid().ToString("N"),
                DisplaySummary = "cleanup", CreatedAt = now, ExpiresAt = now.AddHours(1), RefundId = refund.Id,
            },
            new RefundItem { Id = Guid.NewGuid(), BusinessId = businessId, RefundId = refund.Id, ExternalLineItemId = "cleanup-line", Quantity = 1, Amount = 1m },
            new NotificationRecipient { Id = Guid.NewGuid(), BusinessId = businessId, NotificationId = notification.Id, UserId = user.Id, CreatedAt = now },
            new ConversationHandoff
            {
                Id = Guid.NewGuid(), BusinessId = businessId, ConversationId = conversation.Id, TicketId = ticket.Id,
                FromHandledBy = HandledBy.Ai, ToHandledBy = HandledBy.Human, Reason = "cleanup",
                ActorType = ActorType.BusinessInternalSystem, CreatedAt = now,
            },
            new VerificationCode
            {
                Id = Guid.NewGuid(), BusinessId = businessId, CustomerId = customer.Id, ConversationId = conversation.Id,
                Channel = "email", CodeHash = "cleanup", ExpiresAt = now.AddMinutes(10), CreatedAt = now,
            },
            new CustomerUpdateAction
            {
                Id = Guid.NewGuid(), BusinessId = businessId, CustomerId = customer.Id, ConversationId = conversation.Id,
                FieldsChanged = ["Email"], IdempotencyKey = Guid.NewGuid().ToString(), RequestedAt = now, UpdatedAt = now,
            },
            new FlaggedCustomerActivityLog
            {
                Id = Guid.NewGuid(), BusinessId = businessId, CustomerId = customer.Id, ConversationId = conversation.Id,
                ToolName = "cleanup", Actor = ActorType.BusinessInternalSystem, CreatedAt = now,
            },
            new CustomerExtendedLoggingHistory
            {
                Id = Guid.NewGuid(), BusinessId = businessId, CustomerId = customer.Id, EnabledByUserId = user.Id, EnabledAt = now,
            },
            new ExternalIdentity
            {
                Id = Guid.NewGuid(), BusinessId = businessId, CustomerId = customer.Id, Channel = "web", VerificationMethod = "Otp",
                ExternalUserId = Guid.NewGuid().ToString(), LinkedAt = now,
            },
            new AiProviderInvocation
            {
                Id = Guid.NewGuid(), BusinessId = businessId, Provider = "Anthropic", Model = "cleanup-test", Operation = "cleanup",
                CorrelationId = Guid.NewGuid(), Sequence = 0, ConversationId = conversation.Id, Outcome = "Succeeded", CreatedAt = now,
            },
            new AuditLog
            {
                Id = Guid.NewGuid(), BusinessId = businessId, EntityType = "cleanup", EntityId = "cleanup", EventType = "cleanup",
                Actor = ActorType.BusinessInternalSystem, ConversationId = conversation.Id, CreatedAt = now,
            },
            new ScheduledReportRun
            {
                Id = Guid.NewGuid(), BusinessId = businessId, ReportPeriodStart = now.AddDays(-7), ReportPeriodEnd = now,
                RecipientOwnerId = user.Id, ClaimedAt = now,
            },
            new BusinessSigningKey
            {
                Id = Guid.NewGuid(), BusinessId = businessId, PublicKeyPem = "cleanup", Algorithm = "ES256", CreatedByUserId = user.Id, CreatedAt = now,
            },
            new Integration
            {
                Id = Guid.NewGuid(), BusinessId = businessId, Platform = "cleanup-test", EncryptedAccessToken = "cleanup",
                ConnectedAt = now, UpdatedAt = now,
            },
            new ConsumedSignedIdentityToken { Id = Guid.NewGuid(), BusinessId = businessId, Jti = Guid.NewGuid().ToString(), ConsumedAt = now },
            new SlaTarget { Id = Guid.NewGuid(), BusinessId = businessId, Priority = TicketPriority.Low, TargetMinutes = 60, CreatedAt = now, UpdatedAt = now },
            new TagModeMapping { Id = Guid.NewGuid(), BusinessId = businessId, Tag = "cleanup", Mode = ConversationMode.Auto, CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();

        return businessId;
    }

    private static Widget NewWidget(Guid businessId, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), BusinessId = businessId, Name = "cleanup", PublicKey = $"cleanup-{Guid.NewGuid():N}",
        CreatedAt = now, UpdatedAt = now,
    };

    private static Customer NewCustomer(Guid businessId, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), BusinessId = businessId, ExternalId = $"cleanup-{Guid.NewGuid():N}", CreatedAt = now, UpdatedAt = now,
    };

    private static Conversation NewConversation(Guid businessId, Guid widgetId, Guid? customerId, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), BusinessId = businessId, WidgetId = widgetId, CustomerId = customerId, Channel = "web",
        CreatedAt = now, UpdatedAt = now,
    };

    private static Ticket NewTicket(Guid businessId, Guid customerId, Guid? conversationId, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), BusinessId = businessId, CustomerId = customerId, ConversationId = conversationId,
        Priority = TicketPriority.Low, PriorityReason = "ManualEscalation", Channel = "web", CreatedAt = now, UpdatedAt = now,
    };

    // ── Reading the database ──

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private async Task<long> CountAsync(string sql, Guid id)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<Dictionary<string, long>> CountOwnedRowsAsync(Guid businessId)
    {
        var selects = BusinessIdTables
            .Select(t => $"""SELECT '{t}' AS t, count(*) AS n FROM "{t}" WHERE "BusinessId" = @id""")
            .Concat(IdentityChildTables.Select(t =>
                $"""SELECT '{t}', count(*) FROM "{t}" WHERE "UserId" IN (SELECT "Id" FROM "AspNetUsers" WHERE "BusinessId" = @id)"""))
            .Append("""SELECT 'Businesses', count(*) FROM "Businesses" WHERE "Id" = @id""");

        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(string.Join(" UNION ALL ", selects), connection);
        command.Parameters.AddWithValue("id", businessId);
        var counts = new Dictionary<string, long>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            counts[reader.GetString(0)] = reader.GetInt64(1);
        }
        return counts;
    }

    private static async Task<int> BackendPidAsync(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand("SELECT pg_backend_pid()", connection, transaction);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task InsertAuditLogAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid businessId)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO "AuditLogs" ("Id", "BusinessId", "EntityType", "EntityId", "EventType", "Actor", "CreatedAt")
            VALUES (@id, @businessId, 'cleanup', 'cleanup', 'cleanup', 'BusinessInternalSystem', now())
            """,
            connection, transaction);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("businessId", businessId);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<bool> AnyBackendBlockedByAsync(int blockerPid)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND @blocker = ANY(pg_blocking_pids(pid))",
            connection);
        command.Parameters.AddWithValue("blocker", blockerPid);
        return (long)(await command.ExecuteScalarAsync())! > 0;
    }

    private async Task<bool> IsWaitingOnLockAsync(int pid)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM pg_stat_activity WHERE pid = @pid AND wait_event_type = 'Lock' AND cardinality(pg_blocking_pids(pid)) > 0",
            connection);
        command.Parameters.AddWithValue("pid", pid);
        return (long)(await command.ExecuteScalarAsync())! > 0;
    }

    private static async Task<bool> WaitUntilAsync(TimeSpan deadline, Func<Task<bool>> condition)
    {
        var until = DateTime.UtcNow + deadline;
        while (DateTime.UtcNow < until)
        {
            if (await condition())
            {
                return true;
            }
            await Task.Delay(50);
        }
        return false;
    }

    private sealed class RecordingDisposable(Exception? failure = null) : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return failure is null ? ValueTask.CompletedTask : ValueTask.FromException(failure);
        }
    }

    private sealed class FixedTenantAccessor(Guid? businessId) : ICurrentTenantAccessor
    {
        public Guid? BusinessId => businessId;
    }
}
