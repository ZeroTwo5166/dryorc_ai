using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace backend.Migrations
{
    /// <inheritdoc />
    public partial class AddSignedTokenIdentityInfrastructure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BusinessSigningKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    PublicKeyPem = table.Column<string>(type: "text", nullable: false),
                    Algorithm = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessSigningKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BusinessSigningKeys_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BusinessSigningKeys_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ConsumedSignedIdentityTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    Jti = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsumedSignedIdentityTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConsumedSignedIdentityTokens_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BusinessSigningKeys_BusinessId",
                table: "BusinessSigningKeys",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_BusinessSigningKeys_CreatedByUserId",
                table: "BusinessSigningKeys",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ConsumedSignedIdentityTokens_BusinessId_Jti",
                table: "ConsumedSignedIdentityTokens",
                columns: new[] { "BusinessId", "Jti" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BusinessSigningKeys");

            migrationBuilder.DropTable(
                name: "ConsumedSignedIdentityTokens");
        }
    }
}
