using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiAssistant.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingCancelledStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Question_Status",
                schema: "ai",
                table: "Questions");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Question_Status",
                schema: "ai",
                table: "Questions",
                sql: "\"Status\" IN ('pending', 'ok', 'no_chunks', 'embed_error', 'gen_error', 'cancelled')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Question_Status",
                schema: "ai",
                table: "Questions");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Question_Status",
                schema: "ai",
                table: "Questions",
                sql: "\"Status\" IN ('ok', 'no_chunks', 'embed_error', 'gen_error')");
        }
    }
}
