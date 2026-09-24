using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace backend.Migrations
{
    /// <inheritdoc />
    public partial class AddAiProviderInvocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiProviderInvocations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Model = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Operation = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CorrelationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    InputTokens = table.Column<long>(type: "bigint", nullable: true),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: true),
                    CacheCreationInputTokens = table.Column<long>(type: "bigint", nullable: true),
                    CacheReadInputTokens = table.Column<long>(type: "bigint", nullable: true),
                    InputItemCount = table.Column<int>(type: "integer", nullable: true),
                    Outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    IsUsagePartial = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    ErrorCategory = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiProviderInvocations", x => x.Id);
                    table.CheckConstraint("CK_AiProviderInvocations_ErrorCategory_Allowlist", "\"ErrorCategory\" IS NULL\r\nOR\r\n\"ErrorCategory\" IN ('RateLimited', 'ServerError', 'Timeout', 'Cancelled', 'InvalidRequest', 'Unknown')");
                    table.CheckConstraint("CK_AiProviderInvocations_ErrorCategory_OutcomePairing", "(\"Outcome\" = 'Succeeded' AND \"ErrorCategory\" IS NULL)\r\nOR\r\n(\"Outcome\" != 'Succeeded' AND \"ErrorCategory\" IS NOT NULL)");
                    table.CheckConstraint("CK_AiProviderInvocations_IsUsagePartial_Implication", "\"IsUsagePartial\" = false\r\nOR\r\n(\"Outcome\" = 'Failed' AND \"Provider\" = 'Voyage' AND \"InputTokens\" IS NOT NULL)");
                    table.CheckConstraint("CK_AiProviderInvocations_Outcome", "\"Outcome\" IN ('Succeeded', 'Failed')");
                    table.CheckConstraint("CK_AiProviderInvocations_Provider", "\"Provider\" IN ('Anthropic', 'Voyage')");
                    table.CheckConstraint("CK_AiProviderInvocations_Sequence_NonNegative", "\"Sequence\" >= 0");
                    table.CheckConstraint("CK_AiProviderInvocations_Tokens_NonNegative", "(\"InputTokens\" IS NULL OR \"InputTokens\" >= 0)\r\nAND (\"OutputTokens\" IS NULL OR \"OutputTokens\" >= 0)\r\nAND (\"CacheCreationInputTokens\" IS NULL OR \"CacheCreationInputTokens\" >= 0)\r\nAND (\"CacheReadInputTokens\" IS NULL OR \"CacheReadInputTokens\" >= 0)\r\nAND (\"InputItemCount\" IS NULL OR \"InputItemCount\" >= 0)");
                    table.CheckConstraint("CK_AiProviderInvocations_Voyage_OutputAndCacheTokensNull", "\"Provider\" != 'Voyage'\r\nOR\r\n(\"OutputTokens\" IS NULL AND \"CacheCreationInputTokens\" IS NULL AND \"CacheReadInputTokens\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_AiProviderInvocations_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AiProviderInvocations_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiProviderInvocations_BusinessId_CorrelationId_Sequence",
                table: "AiProviderInvocations",
                columns: new[] { "BusinessId", "CorrelationId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiProviderInvocations_BusinessId_CreatedAt",
                table: "AiProviderInvocations",
                columns: new[] { "BusinessId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AiProviderInvocations_ConversationId",
                table: "AiProviderInvocations",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_AiProviderInvocations_CreatedAt",
                table: "AiProviderInvocations",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiProviderInvocations");
        }
    }
}
