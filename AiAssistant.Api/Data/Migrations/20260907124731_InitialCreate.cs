using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AiAssistant.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ai");

            migrationBuilder.CreateTable(
                name: "Questions",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TicketScope = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    UserId = table.Column<int>(type: "integer", nullable: true),
                    QuestionText = table.Column<string>(type: "text", nullable: false),
                    AnswerText = table.Column<string>(type: "text", nullable: true),
                    SystemPromptUsed = table.Column<string>(type: "text", nullable: false),
                    ContextUsed = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LanguageDriftSuspected = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Questions", x => x.Id);
                    table.CheckConstraint("CK_Question_Status", "\"Status\" IN ('ok', 'no_chunks', 'embed_error', 'gen_error')");
                });

            migrationBuilder.CreateTable(
                name: "Rubrics",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    QuestionText = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Rubrics", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Sites",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    BaseUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sites", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SysParams",
                schema: "ai",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SysParams", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "FeedbackMarks",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    QuestionId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    Code = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Comment = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeedbackMarks", x => x.Id);
                    table.CheckConstraint("CK_FeedbackMark_Code", "\"Code\" IN ('green', 'red')");
                    table.ForeignKey(
                        name: "FK_FeedbackMarks_Questions_QuestionId",
                        column: x => x.QuestionId,
                        principalSchema: "ai",
                        principalTable: "Questions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TargetPages",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SiteId = table.Column<int>(type: "integer", nullable: false),
                    Url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TargetPages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TargetPages_Sites_SiteId",
                        column: x => x.SiteId,
                        principalSchema: "ai",
                        principalTable: "Sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExtractedContents",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TargetPageId = table.Column<int>(type: "integer", nullable: true),
                    SourceFileName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    StorageObjectKey = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Text = table.Column<string>(type: "text", nullable: false),
                    Language = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExtractedContents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExtractedContents_TargetPages_TargetPageId",
                        column: x => x.TargetPageId,
                        principalSchema: "ai",
                        principalTable: "TargetPages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ContentChunks",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ExtractedContentId = table.Column<int>(type: "integer", nullable: false),
                    QdrantPointId = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    Scope = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Language = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContentChunks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContentChunks_ExtractedContents_ExtractedContentId",
                        column: x => x.ExtractedContentId,
                        principalSchema: "ai",
                        principalTable: "ExtractedContents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RequestChunks",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    QuestionId = table.Column<int>(type: "integer", nullable: false),
                    ContentChunkId = table.Column<int>(type: "integer", nullable: false),
                    Score = table.Column<float>(type: "real", nullable: false),
                    Rank = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestChunks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RequestChunks_ContentChunks_ContentChunkId",
                        column: x => x.ContentChunkId,
                        principalSchema: "ai",
                        principalTable: "ContentChunks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RequestChunks_Questions_QuestionId",
                        column: x => x.QuestionId,
                        principalSchema: "ai",
                        principalTable: "Questions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContentChunks_ExtractedContentId",
                schema: "ai",
                table: "ContentChunks",
                column: "ExtractedContentId");

            migrationBuilder.CreateIndex(
                name: "IX_ContentChunks_QdrantPointId",
                schema: "ai",
                table: "ContentChunks",
                column: "QdrantPointId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContentChunks_Scope",
                schema: "ai",
                table: "ContentChunks",
                column: "Scope");

            migrationBuilder.CreateIndex(
                name: "IX_ExtractedContents_TargetPageId",
                schema: "ai",
                table: "ExtractedContents",
                column: "TargetPageId");

            migrationBuilder.CreateIndex(
                name: "IX_FeedbackMarks_QuestionId",
                schema: "ai",
                table: "FeedbackMarks",
                column: "QuestionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Questions_CreatedAt",
                schema: "ai",
                table: "Questions",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Questions_TicketScope",
                schema: "ai",
                table: "Questions",
                column: "TicketScope");

            migrationBuilder.CreateIndex(
                name: "IX_RequestChunks_ContentChunkId",
                schema: "ai",
                table: "RequestChunks",
                column: "ContentChunkId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestChunks_QuestionId",
                schema: "ai",
                table: "RequestChunks",
                column: "QuestionId");

            migrationBuilder.CreateIndex(
                name: "IX_Sites_Name",
                schema: "ai",
                table: "Sites",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_TargetPages_SiteId",
                schema: "ai",
                table: "TargetPages",
                column: "SiteId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FeedbackMarks",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "RequestChunks",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "Rubrics",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "SysParams",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "ContentChunks",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "Questions",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "ExtractedContents",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "TargetPages",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "Sites",
                schema: "ai");
        }
    }
}
