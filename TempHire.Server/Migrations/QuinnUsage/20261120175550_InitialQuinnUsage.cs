using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TempHire.Server.Migrations.QuinnUsage;

/// <inheritdoc />
public partial class InitialQuinnUsage : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "QuinnUsage",
            columns: table => new
            {
                TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Provider = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                Model = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                Day = table.Column<DateOnly>(type: "date", nullable: false),
                InputTokens = table.Column<long>(type: "bigint", nullable: false),
                OutputTokens = table.Column<long>(type: "bigint", nullable: false),
                CostMicrodollars = table.Column<long>(type: "bigint", nullable: false),
                Questions = table.Column<int>(type: "int", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_QuinnUsage", x => new { x.TenantId, x.UserId, x.Provider, x.Model, x.Day });
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "QuinnUsage");
    }
}
