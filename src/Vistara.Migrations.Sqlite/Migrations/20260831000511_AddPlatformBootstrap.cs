using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vistara.Migrations.Sqlite.Migrations;

/// <inheritdoc />
public partial class AddPlatformBootstrap : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "platform_bootstrap",
            columns: table => new
            {
                id = table.Column<int>(type: "INTEGER", nullable: false),
                owner_tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                owner_user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                provisioned_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                version = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_platform_bootstrap", x => x.id);
                table.CheckConstraint("ck_platform_bootstrap_singleton", "\"id\" = 1");
                table.CheckConstraint("ck_platform_bootstrap_version", "\"version\" >= 1");
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "platform_bootstrap");
    }
}
