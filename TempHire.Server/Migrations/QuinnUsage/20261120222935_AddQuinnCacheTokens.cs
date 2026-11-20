using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TempHire.Server.Migrations.QuinnUsage
{
    /// <inheritdoc />
    public partial class AddQuinnCacheTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CacheReadTokens",
                table: "QuinnUsage",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "CacheWriteTokens",
                table: "QuinnUsage",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CacheReadTokens",
                table: "QuinnUsage");

            migrationBuilder.DropColumn(
                name: "CacheWriteTokens",
                table: "QuinnUsage");
        }
    }
}
