using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vistara.Migrations.Postgres.Migrations;

/// <inheritdoc />
public partial class AddLocalCredentials : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "local_credentials",
            columns: table => new
            {
                local_identity_id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                password_hash = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_local_credentials", x => x.local_identity_id);
                table.CheckConstraint("ck_local_credentials_version", "\"version\" >= 1");
                table.ForeignKey(
                    name: "FK_local_credentials_local_identities_local_identity_id",
                    column: x => x.local_identity_id,
                    principalTable: "local_identities",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_local_credentials_users_user_id",
                    column: x => x.user_id,
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_local_credentials_user_id",
            table: "local_credentials",
            column: "user_id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "local_credentials");
    }
}
