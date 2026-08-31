using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vistara.Migrations.Postgres.Migrations;

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
                id = table.Column<int>(type: "integer", nullable: false),
                owner_tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                provisioned_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_platform_bootstrap", x => x.id);
                table.CheckConstraint("ck_platform_bootstrap_singleton", "\"id\" = 1");
                table.CheckConstraint("ck_platform_bootstrap_version", "\"version\" >= 1");
            });

        PostgresTenantRowSecurity.EnableIdentityDirectory(migrationBuilder);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        PostgresTenantRowSecurity.DisableIdentityDirectory(migrationBuilder);
        migrationBuilder.DropTable(
            name: "platform_bootstrap");
    }
}
