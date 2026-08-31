using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vistara.Migrations.Postgres.Migrations;

/// <inheritdoc />
public partial class AddUserPreferences : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "user_preferences",
            columns: table => new
            {
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                density = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                reduced_motion = table.Column<bool>(type: "boolean", nullable: false),
                screen_reader_paged_mode = table.Column<bool>(type: "boolean", nullable: false),
                locale = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: true),
                time_zone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_user_preferences", x => x.user_id);
                table.CheckConstraint("ck_user_preferences_density", "\"density\" IN ('comfortable','compact')");
                table.CheckConstraint("ck_user_preferences_version", "\"version\" >= 1");
                table.ForeignKey(
                    name: "FK_user_preferences_users_user_id",
                    column: x => x.user_id,
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "user_preferences");
    }
}
