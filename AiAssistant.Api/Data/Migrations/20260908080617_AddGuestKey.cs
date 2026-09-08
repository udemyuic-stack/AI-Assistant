using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiAssistant.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGuestKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GuestKey",
                schema: "ai",
                table: "Questions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Questions_GuestKey_CreatedAt",
                schema: "ai",
                table: "Questions",
                columns: new[] { "GuestKey", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Questions_GuestKey_CreatedAt",
                schema: "ai",
                table: "Questions");

            migrationBuilder.DropColumn(
                name: "GuestKey",
                schema: "ai",
                table: "Questions");
        }
    }
}
