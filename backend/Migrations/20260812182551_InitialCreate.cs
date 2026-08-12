using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Pgvector;

#nullable disable

namespace backend.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AspNetRoles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Businesses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    RetentionDays = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PlanTier = table.Column<string>(type: "text", nullable: true),
                    TimeZone = table.Column<string>(type: "text", nullable: false),
                    ContactEmail = table.Column<string>(type: "text", nullable: true),
                    ContactPhone = table.Column<string>(type: "text", nullable: true),
                    VatNumber = table.Column<string>(type: "text", nullable: true),
                    ExtendedAuditLoggingEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CancelOrderRestocksInventory = table.Column<bool>(type: "boolean", nullable: false),
                    AutoRefundLimit = table.Column<decimal>(type: "numeric", nullable: true),
                    AutoRefundLimitCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    StoreCreditRefundsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Businesses", x => x.Id);
                    table.CheckConstraint("CK_Business_AutoRefundLimit_NonNegative", "\"AutoRefundLimit\" IS NULL OR \"AutoRefundLimit\" >= 0");
                    table.CheckConstraint("CK_Business_AutoRefundLimit_Pairing", "(\"AutoRefundLimit\" IS NULL AND \"AutoRefundLimitCurrency\" IS NULL) OR (\"AutoRefundLimit\" IS NOT NULL AND \"AutoRefundLimitCurrency\" IS NOT NULL)");
                    table.CheckConstraint("CK_Business_RetentionDays_Range", "\"RetentionDays\" BETWEEN 30 AND 730");
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoleClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoleClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetRoleClaims_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: true),
                    SecurityStamp = table.Column<string>(type: "text", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true),
                    PhoneNumber = table.Column<string>(type: "text", nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUsers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetUsers_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Customers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalId = table.Column<string>(type: "text", nullable: true),
                    Email = table.Column<string>(type: "text", nullable: true),
                    Name = table.Column<string>(type: "text", nullable: true),
                    IsAnonymized = table.Column<bool>(type: "boolean", nullable: false),
                    AnonymizedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExtendedLoggingEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Customers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Customers_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Integrations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    Platform = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    EncryptedAccessToken = table.Column<string>(type: "text", nullable: false),
                    EncryptedRefreshToken = table.Column<string>(type: "text", nullable: true),
                    ExternalAccountIdentifier = table.Column<string>(type: "text", nullable: true),
                    Scopes = table.Column<List<string>>(type: "text[]", nullable: false),
                    ScopesUpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConnectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DisconnectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TokenExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Integrations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Integrations_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "KnowledgeDocuments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KnowledgeDocuments_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SlaTargets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    Priority = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Channel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    TargetMinutes = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlaTargets", x => x.Id);
                    table.CheckConstraint("CK_SlaTargets_TargetMinutes_Positive", "\"TargetMinutes\" > 0");
                    table.ForeignKey(
                        name: "FK_SlaTargets_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TagModeMappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    Tag = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Mode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IsMinimumFloor = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TagModeMappings", x => x.Id);
                    table.CheckConstraint("CK_TagModeMappings_Tag_NotEmpty", "length(trim(\"Tag\")) > 0");
                    table.ForeignKey(
                        name: "FK_TagModeMappings_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Widgets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    PublicKey = table.Column<string>(type: "text", nullable: false),
                    AllowedDomains = table.Column<List<string>>(type: "text[]", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Widgets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Widgets_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetUserClaims_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserLogins",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    ProviderKey = table.Column<string>(type: "text", nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "text", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserLogins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_AspNetUserLogins_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserRoles",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserTokens",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_AspNetUserTokens_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CustomerExtendedLoggingHistories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnabledByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnabledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DisabledByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DisabledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerExtendedLoggingHistories", x => x.Id);
                    table.CheckConstraint("CK_CustomerExtendedLoggingHistory_DisabledPairing", "(\"DisabledByUserId\" IS NULL AND \"DisabledAt\" IS NULL) OR (\"DisabledByUserId\" IS NOT NULL AND \"DisabledAt\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_CustomerExtendedLoggingHistories_AspNetUsers_DisabledByUser~",
                        column: x => x.DisabledByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_CustomerExtendedLoggingHistories_AspNetUsers_EnabledByUserId",
                        column: x => x.EnabledByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerExtendedLoggingHistories_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerExtendedLoggingHistories_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExternalIdentities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Channel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    VerificationMethod = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ExternalUserId = table.Column<string>(type: "text", nullable: false),
                    LinkedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalIdentities", x => x.Id);
                    table.CheckConstraint("CK_ExternalIdentities_VerificationMethod", "\"VerificationMethod\" IN ('SignedToken', 'Otp')");
                    table.ForeignKey(
                        name: "FK_ExternalIdentities_AspNetUsers_RevokedByUserId",
                        column: x => x.RevokedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ExternalIdentities_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ExternalIdentities_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "KnowledgeChunks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    KnowledgeDocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChunkIndex = table.Column<int>(type: "integer", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    Embedding = table.Column<Vector>(type: "vector(1024)", nullable: false),
                    EmbeddingModel = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeChunks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KnowledgeChunks_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_KnowledgeChunks_KnowledgeDocuments_KnowledgeDocumentId",
                        column: x => x.KnowledgeDocumentId,
                        principalTable: "KnowledgeDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Conversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    WidgetId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: true),
                    VisitorSessionId = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    HandledBy = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Channel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Tags = table.Column<List<string>>(type: "text[]", nullable: false),
                    Level2VerifiedUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastMessageAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Conversations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Conversations_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Conversations_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Conversations_Widgets_WidgetId",
                        column: x => x.WidgetId,
                        principalTable: "Widgets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AuditLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityType = table.Column<string>(type: "text", nullable: false),
                    EntityId = table.Column<string>(type: "text", nullable: false),
                    EventType = table.Column<string>(type: "text", nullable: false),
                    OldValue = table.Column<string>(type: "text", nullable: true),
                    NewValue = table.Column<string>(type: "text", nullable: true),
                    Actor = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ChangedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    CorrelationId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuditLogs_AspNetUsers_ChangedByUserId",
                        column: x => x.ChangedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AuditLogs_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AuditLogs_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "CustomerUpdateActions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    FieldsChanged = table.Column<List<string>>(type: "text[]", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FailureReason = table.Column<string>(type: "text", nullable: true),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerUpdateActions", x => x.Id);
                    table.CheckConstraint("CK_CustomerUpdateActions_CompletedAt", "(\"Status\" = 'Confirmed' AND \"CompletedAt\" IS NOT NULL)\r\nOR\r\n(\"Status\" <> 'Confirmed' AND \"CompletedAt\" IS NULL)");
                    table.CheckConstraint("CK_CustomerUpdateActions_FieldsChanged", "cardinality(\"FieldsChanged\") > 0");
                    table.ForeignKey(
                        name: "FK_CustomerUpdateActions_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerUpdateActions_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_CustomerUpdateActions_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FlaggedCustomerActivityLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    ToolName = table.Column<string>(type: "text", nullable: false),
                    Parameters = table.Column<string>(type: "text", nullable: true),
                    Actor = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PerformedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlaggedCustomerActivityLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FlaggedCustomerActivityLogs_AspNetUsers_PerformedByUserId",
                        column: x => x.PerformedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_FlaggedCustomerActivityLogs_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FlaggedCustomerActivityLogs_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_FlaggedCustomerActivityLogs_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExternalCreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AuthorUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Messages", x => x.Id);
                    table.CheckConstraint("CK_Messages_AuthorUserId_RolePairing", "(\"Role\" = 'Agent' AND \"AuthorUserId\" IS NOT NULL) OR (\"Role\" != 'Agent' AND \"AuthorUserId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_Messages_AspNetUsers_AuthorUserId",
                        column: x => x.AuthorUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Messages_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Messages_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OrderCancellations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExternalOrderId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CustomerReason = table.Column<string>(type: "text", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Restock = table.Column<bool>(type: "boolean", nullable: false),
                    Refund = table.Column<bool>(type: "boolean", nullable: false),
                    FailureReason = table.Column<string>(type: "text", nullable: true),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderCancellations", x => x.Id);
                    table.CheckConstraint("CK_OrderCancellations_CompletedAt", "(\"Status\" = 'Confirmed' AND \"CompletedAt\" IS NOT NULL)\r\nOR\r\n(\"Status\" <> 'Confirmed' AND \"CompletedAt\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_OrderCancellations_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OrderCancellations_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_OrderCancellations_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Refunds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExternalOrderId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ExternalRefundId = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Method = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    TotalAmount = table.Column<decimal>(type: "numeric", nullable: true),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    FailureReason = table.Column<string>(type: "text", nullable: true),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Refunds", x => x.Id);
                    table.CheckConstraint("CK_Refunds_CompletedAt", "(\"Status\" = 'Confirmed' AND \"CompletedAt\" IS NOT NULL)\r\nOR\r\n(\"Status\" <> 'Confirmed' AND \"CompletedAt\" IS NULL)");
                    table.CheckConstraint("CK_Refunds_TotalAmount_NonNegative", "\"TotalAmount\" IS NULL OR \"TotalAmount\" >= 0");
                    table.ForeignKey(
                        name: "FK_Refunds_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Refunds_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Refunds_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Tickets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Priority = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PriorityReason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Channel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ClaimedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClaimedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ResolvedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FirstRespondedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SlaDeadline = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Summary = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tickets", x => x.Id);
                    table.CheckConstraint("CK_Tickets_FirstRespondedAt_After_CreatedAt", "\"FirstRespondedAt\" IS NULL OR \"FirstRespondedAt\" >= \"CreatedAt\"");
                    table.CheckConstraint("CK_Tickets_PriorityReason", "\"PriorityReason\" IN ('RefundDispute', 'ManualEscalation', 'RefundLimitReached')");
                    table.ForeignKey(
                        name: "FK_Tickets_AspNetUsers_ClaimedByUserId",
                        column: x => x.ClaimedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Tickets_AspNetUsers_ResolvedByUserId",
                        column: x => x.ResolvedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Tickets_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Tickets_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Tickets_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VerificationCodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Channel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CodeHash = table.Column<string>(type: "text", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Used = table.Column<bool>(type: "boolean", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    SupersededAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VerificationCodes", x => x.Id);
                    table.CheckConstraint("CK_VerificationCodes_Attempts", "\"AttemptCount\" >= 0 AND \"MaxAttempts\" > 0 AND \"AttemptCount\" <= \"MaxAttempts\"");
                    table.ForeignKey(
                        name: "FK_VerificationCodes_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VerificationCodes_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VerificationCodes_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MessageKnowledgeSources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    KnowledgeDocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    KnowledgeChunkId = table.Column<Guid>(type: "uuid", nullable: true),
                    DocumentTitleSnapshot = table.Column<string>(type: "text", nullable: false),
                    ChunkContentSnapshot = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageKnowledgeSources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MessageKnowledgeSources_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MessageKnowledgeSources_KnowledgeChunks_KnowledgeChunkId",
                        column: x => x.KnowledgeChunkId,
                        principalTable: "KnowledgeChunks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MessageKnowledgeSources_KnowledgeDocuments_KnowledgeDocumen~",
                        column: x => x.KnowledgeDocumentId,
                        principalTable: "KnowledgeDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MessageKnowledgeSources_Messages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "Messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RefundItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    RefundId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalLineItemId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefundItems", x => x.Id);
                    table.CheckConstraint("CK_RefundItems_Amount_Positive", "\"Amount\" > 0 AND \"Quantity\" > 0");
                    table.ForeignKey(
                        name: "FK_RefundItems_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RefundItems_Refunds_RefundId",
                        column: x => x.RefundId,
                        principalTable: "Refunds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConversationHandoffs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    TicketId = table.Column<Guid>(type: "uuid", nullable: true),
                    FromHandledBy = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ToHandledBy = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ActorType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationHandoffs", x => x.Id);
                    table.CheckConstraint("CK_ConversationHandoffs_Actor", "(\"ActorType\" = 'NivoraBusinessStaff' AND \"ActorId\" IS NOT NULL)\r\nOR\r\n(\"ActorType\" <> 'NivoraBusinessStaff' AND \"ActorId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_ConversationHandoffs_AspNetUsers_ActorId",
                        column: x => x.ActorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ConversationHandoffs_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConversationHandoffs_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ConversationHandoffs_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AspNetRoleClaims_RoleId",
                table: "AspNetRoleClaims",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                table: "AspNetRoles",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserClaims_UserId",
                table: "AspNetUserClaims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserLogins_UserId",
                table: "AspNetUserLogins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserRoles_RoleId",
                table: "AspNetUserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_BusinessId",
                table: "AspNetUsers",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "AspNetUsers",
                column: "NormalizedUserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_BusinessId_CreatedAt",
                table: "AuditLogs",
                columns: new[] { "BusinessId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_BusinessId_EntityType_EntityId_CreatedAt",
                table: "AuditLogs",
                columns: new[] { "BusinessId", "EntityType", "EntityId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_ChangedByUserId",
                table: "AuditLogs",
                column: "ChangedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_ConversationId",
                table: "AuditLogs",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_CorrelationId",
                table: "AuditLogs",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationHandoffs_ActorId",
                table: "ConversationHandoffs",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationHandoffs_BusinessId_ActorId",
                table: "ConversationHandoffs",
                columns: new[] { "BusinessId", "ActorId" });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationHandoffs_BusinessId_ConversationId_CreatedAt",
                table: "ConversationHandoffs",
                columns: new[] { "BusinessId", "ConversationId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationHandoffs_BusinessId_TicketId",
                table: "ConversationHandoffs",
                columns: new[] { "BusinessId", "TicketId" });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationHandoffs_ConversationId",
                table: "ConversationHandoffs",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationHandoffs_TicketId",
                table: "ConversationHandoffs",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_BusinessId_CreatedAt",
                table: "Conversations",
                columns: new[] { "BusinessId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_BusinessId_CustomerId",
                table: "Conversations",
                columns: new[] { "BusinessId", "CustomerId" });

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_BusinessId_LastMessageAt",
                table: "Conversations",
                columns: new[] { "BusinessId", "LastMessageAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_CustomerId",
                table: "Conversations",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_VisitorSessionId",
                table: "Conversations",
                column: "VisitorSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_WidgetId",
                table: "Conversations",
                column: "WidgetId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerExtendedLoggingHistories_BusinessId_CustomerId",
                table: "CustomerExtendedLoggingHistories",
                columns: new[] { "BusinessId", "CustomerId" },
                unique: true,
                filter: "\"DisabledAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerExtendedLoggingHistories_BusinessId_CustomerId_Enab~",
                table: "CustomerExtendedLoggingHistories",
                columns: new[] { "BusinessId", "CustomerId", "EnabledAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerExtendedLoggingHistories_CustomerId",
                table: "CustomerExtendedLoggingHistories",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerExtendedLoggingHistories_DisabledByUserId",
                table: "CustomerExtendedLoggingHistories",
                column: "DisabledByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerExtendedLoggingHistories_EnabledByUserId",
                table: "CustomerExtendedLoggingHistories",
                column: "EnabledByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_BusinessId",
                table: "Customers",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_BusinessId_Email",
                table: "Customers",
                columns: new[] { "BusinessId", "Email" });

            migrationBuilder.CreateIndex(
                name: "IX_Customers_BusinessId_ExternalId",
                table: "Customers",
                columns: new[] { "BusinessId", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerUpdateActions_BusinessId_ConversationId",
                table: "CustomerUpdateActions",
                columns: new[] { "BusinessId", "ConversationId" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerUpdateActions_BusinessId_CustomerId",
                table: "CustomerUpdateActions",
                columns: new[] { "BusinessId", "CustomerId" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerUpdateActions_BusinessId_IdempotencyKey",
                table: "CustomerUpdateActions",
                columns: new[] { "BusinessId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerUpdateActions_ConversationId",
                table: "CustomerUpdateActions",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerUpdateActions_CustomerId",
                table: "CustomerUpdateActions",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalIdentities_BusinessId_Channel_ExternalUserId",
                table: "ExternalIdentities",
                columns: new[] { "BusinessId", "Channel", "ExternalUserId" },
                unique: true,
                filter: "\"RevokedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalIdentities_BusinessId_CustomerId",
                table: "ExternalIdentities",
                columns: new[] { "BusinessId", "CustomerId" });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalIdentities_CustomerId",
                table: "ExternalIdentities",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalIdentities_RevokedByUserId",
                table: "ExternalIdentities",
                column: "RevokedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_FlaggedCustomerActivityLogs_BusinessId_CustomerId_CreatedAt",
                table: "FlaggedCustomerActivityLogs",
                columns: new[] { "BusinessId", "CustomerId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FlaggedCustomerActivityLogs_ConversationId",
                table: "FlaggedCustomerActivityLogs",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_FlaggedCustomerActivityLogs_CustomerId",
                table: "FlaggedCustomerActivityLogs",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_FlaggedCustomerActivityLogs_PerformedByUserId",
                table: "FlaggedCustomerActivityLogs",
                column: "PerformedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Integrations_BusinessId_Platform",
                table: "Integrations",
                columns: new[] { "BusinessId", "Platform" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Integrations_Platform_ExternalAccountIdentifier",
                table: "Integrations",
                columns: new[] { "Platform", "ExternalAccountIdentifier" },
                unique: true,
                filter: "\"ExternalAccountIdentifier\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeChunks_BusinessId",
                table: "KnowledgeChunks",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeChunks_Embedding",
                table: "KnowledgeChunks",
                column: "Embedding")
                .Annotation("Npgsql:IndexMethod", "hnsw")
                .Annotation("Npgsql:IndexOperators", new[] { "vector_cosine_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeChunks_KnowledgeDocumentId_ChunkIndex",
                table: "KnowledgeChunks",
                columns: new[] { "KnowledgeDocumentId", "ChunkIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeDocuments_BusinessId",
                table: "KnowledgeDocuments",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeDocuments_BusinessId_UpdatedAt",
                table: "KnowledgeDocuments",
                columns: new[] { "BusinessId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MessageKnowledgeSources_BusinessId_MessageId",
                table: "MessageKnowledgeSources",
                columns: new[] { "BusinessId", "MessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_MessageKnowledgeSources_KnowledgeChunkId",
                table: "MessageKnowledgeSources",
                column: "KnowledgeChunkId");

            migrationBuilder.CreateIndex(
                name: "IX_MessageKnowledgeSources_KnowledgeDocumentId",
                table: "MessageKnowledgeSources",
                column: "KnowledgeDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_MessageKnowledgeSources_MessageId",
                table: "MessageKnowledgeSources",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_AuthorUserId",
                table: "Messages",
                column: "AuthorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_BusinessId_ConversationId_CreatedAt",
                table: "Messages",
                columns: new[] { "BusinessId", "ConversationId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_BusinessId_CreatedAt",
                table: "Messages",
                columns: new[] { "BusinessId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ConversationId",
                table: "Messages",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderCancellations_BusinessId_CustomerId",
                table: "OrderCancellations",
                columns: new[] { "BusinessId", "CustomerId" });

            migrationBuilder.CreateIndex(
                name: "IX_OrderCancellations_BusinessId_ExternalOrderId",
                table: "OrderCancellations",
                columns: new[] { "BusinessId", "ExternalOrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_OrderCancellations_BusinessId_IdempotencyKey",
                table: "OrderCancellations",
                columns: new[] { "BusinessId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrderCancellations_ConversationId",
                table: "OrderCancellations",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderCancellations_CustomerId",
                table: "OrderCancellations",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_RefundItems_BusinessId_RefundId_ExternalLineItemId",
                table: "RefundItems",
                columns: new[] { "BusinessId", "RefundId", "ExternalLineItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RefundItems_RefundId",
                table: "RefundItems",
                column: "RefundId");

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_BusinessId_CustomerId",
                table: "Refunds",
                columns: new[] { "BusinessId", "CustomerId" });

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_BusinessId_ExternalOrderId",
                table: "Refunds",
                columns: new[] { "BusinessId", "ExternalOrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_BusinessId_IdempotencyKey",
                table: "Refunds",
                columns: new[] { "BusinessId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_ConversationId",
                table: "Refunds",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_CustomerId",
                table: "Refunds",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_SlaTargets_BusinessId_Priority",
                table: "SlaTargets",
                columns: new[] { "BusinessId", "Priority" },
                unique: true,
                filter: "\"Channel\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SlaTargets_BusinessId_Priority_Channel",
                table: "SlaTargets",
                columns: new[] { "BusinessId", "Priority", "Channel" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TagModeMappings_BusinessId_Tag",
                table: "TagModeMappings",
                columns: new[] { "BusinessId", "Tag" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_BusinessId_ClaimedByUserId",
                table: "Tickets",
                columns: new[] { "BusinessId", "ClaimedByUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_BusinessId_CustomerId",
                table: "Tickets",
                columns: new[] { "BusinessId", "CustomerId" });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_BusinessId_Status_Priority_CreatedAt",
                table: "Tickets",
                columns: new[] { "BusinessId", "Status", "Priority", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_ClaimedByUserId",
                table: "Tickets",
                column: "ClaimedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_ConversationId",
                table: "Tickets",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_CustomerId",
                table: "Tickets",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_ResolvedByUserId",
                table: "Tickets",
                column: "ResolvedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_VerificationCodes_BusinessId_ConversationId",
                table: "VerificationCodes",
                columns: new[] { "BusinessId", "ConversationId" });

            migrationBuilder.CreateIndex(
                name: "IX_VerificationCodes_BusinessId_CustomerId_ConversationId_Chan~",
                table: "VerificationCodes",
                columns: new[] { "BusinessId", "CustomerId", "ConversationId", "Channel", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_VerificationCodes_ConversationId",
                table: "VerificationCodes",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_VerificationCodes_CustomerId",
                table: "VerificationCodes",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Widgets_BusinessId",
                table: "Widgets",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_Widgets_PublicKey",
                table: "Widgets",
                column: "PublicKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AspNetRoleClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserLogins");

            migrationBuilder.DropTable(
                name: "AspNetUserRoles");

            migrationBuilder.DropTable(
                name: "AspNetUserTokens");

            migrationBuilder.DropTable(
                name: "AuditLogs");

            migrationBuilder.DropTable(
                name: "ConversationHandoffs");

            migrationBuilder.DropTable(
                name: "CustomerExtendedLoggingHistories");

            migrationBuilder.DropTable(
                name: "CustomerUpdateActions");

            migrationBuilder.DropTable(
                name: "ExternalIdentities");

            migrationBuilder.DropTable(
                name: "FlaggedCustomerActivityLogs");

            migrationBuilder.DropTable(
                name: "Integrations");

            migrationBuilder.DropTable(
                name: "MessageKnowledgeSources");

            migrationBuilder.DropTable(
                name: "OrderCancellations");

            migrationBuilder.DropTable(
                name: "RefundItems");

            migrationBuilder.DropTable(
                name: "SlaTargets");

            migrationBuilder.DropTable(
                name: "TagModeMappings");

            migrationBuilder.DropTable(
                name: "VerificationCodes");

            migrationBuilder.DropTable(
                name: "AspNetRoles");

            migrationBuilder.DropTable(
                name: "Tickets");

            migrationBuilder.DropTable(
                name: "KnowledgeChunks");

            migrationBuilder.DropTable(
                name: "Messages");

            migrationBuilder.DropTable(
                name: "Refunds");

            migrationBuilder.DropTable(
                name: "KnowledgeDocuments");

            migrationBuilder.DropTable(
                name: "AspNetUsers");

            migrationBuilder.DropTable(
                name: "Conversations");

            migrationBuilder.DropTable(
                name: "Customers");

            migrationBuilder.DropTable(
                name: "Widgets");

            migrationBuilder.DropTable(
                name: "Businesses");
        }
    }
}
