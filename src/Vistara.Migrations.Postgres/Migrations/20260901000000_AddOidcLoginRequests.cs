using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vistara.Migrations.Postgres.Migrations;

/// <inheritdoc />
public partial class AddOidcLoginRequests : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "oidc_login_requests",
            columns: table => new
            {
                state_digest = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                provider_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                nonce_digest = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                handle_digest = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                code_verifier = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                redirect_uri = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                return_to = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                consumed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_oidc_login_requests", x => x.state_digest);
                table.CheckConstraint("ck_oidc_login_requests_consumed", "\"consumed_at_utc\" IS NULL OR \"consumed_at_utc\" >= \"created_at_utc\"");
                table.CheckConstraint("ck_oidc_login_requests_lifetime", "\"expires_at_utc\" > \"created_at_utc\"");
            });

        migrationBuilder.CreateIndex(
            name: "ix_oidc_login_requests_expires_at_utc",
            table: "oidc_login_requests",
            column: "expires_at_utc");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "oidc_login_requests");
    }
}
