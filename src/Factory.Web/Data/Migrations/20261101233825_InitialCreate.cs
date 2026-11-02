using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Factory.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tasks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    RepositoryPath = table.Column<string>(type: "TEXT", nullable: false),
                    ArchitectureSpec = table.Column<string>(type: "TEXT", nullable: false),
                    VerificationCommand = table.Column<string>(type: "TEXT", nullable: false),
                    RequirePlanApproval = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Runs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TaskId = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    CurrentStage = table.Column<string>(type: "TEXT", nullable: true),
                    CurrentSubtaskId = table.Column<int>(type: "INTEGER", nullable: true),
                    PausedStage = table.Column<string>(type: "TEXT", nullable: true),
                    PauseReason = table.Column<string>(type: "TEXT", nullable: true),
                    BranchName = table.Column<string>(type: "TEXT", nullable: true),
                    BaseBranch = table.Column<string>(type: "TEXT", nullable: true),
                    BaseCommit = table.Column<string>(type: "TEXT", nullable: true),
                    RepoMap = table.Column<string>(type: "TEXT", nullable: false),
                    IntegrationPassed = table.Column<bool>(type: "INTEGER", nullable: true),
                    MergeCommit = table.Column<string>(type: "TEXT", nullable: true),
                    PromptTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    CompletionTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Runs_Tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RunId = table.Column<int>(type: "INTEGER", nullable: false),
                    SubtaskId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Stage = table.Column<string>(type: "TEXT", nullable: true),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    Role = table.Column<string>(type: "TEXT", nullable: true),
                    ReasoningEffort = table.Column<string>(type: "TEXT", nullable: true),
                    Temperature = table.Column<double>(type: "REAL", nullable: true),
                    Prompt = table.Column<string>(type: "TEXT", nullable: true),
                    Thinking = table.Column<string>(type: "TEXT", nullable: true),
                    Response = table.Column<string>(type: "TEXT", nullable: true),
                    PromptTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    CompletionTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    DurationMs = table.Column<long>(type: "INTEGER", nullable: false),
                    Succeeded = table.Column<bool>(type: "INTEGER", nullable: false),
                    Command = table.Column<string>(type: "TEXT", nullable: true),
                    ExitCode = table.Column<int>(type: "INTEGER", nullable: true),
                    Output = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentLogs_Runs_RunId",
                        column: x => x.RunId,
                        principalTable: "Runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Diffs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RunId = table.Column<int>(type: "INTEGER", nullable: false),
                    SubtaskId = table.Column<int>(type: "INTEGER", nullable: true),
                    Stage = table.Column<string>(type: "TEXT", nullable: false),
                    Attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    UnifiedDiff = table.Column<string>(type: "TEXT", nullable: false),
                    CommitSha = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Diffs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Diffs_Runs_RunId",
                        column: x => x.RunId,
                        principalTable: "Runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Subtasks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RunId = table.Column<int>(type: "INTEGER", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    TargetFilesToModify = table.Column<string>(type: "TEXT", nullable: false),
                    TargetFilesToCreate = table.Column<string>(type: "TEXT", nullable: false),
                    ContextFilesToRead = table.Column<string>(type: "TEXT", nullable: false),
                    VerificationCommand = table.Column<string>(type: "TEXT", nullable: false),
                    CommandApproved = table.Column<bool>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    FixAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LastFailure = table.Column<string>(type: "TEXT", nullable: true),
                    HumanGuidance = table.Column<string>(type: "TEXT", nullable: true),
                    RefinedSpecJson = table.Column<string>(type: "TEXT", nullable: true),
                    SummaryOfChanges = table.Column<string>(type: "TEXT", nullable: true),
                    ReviewJson = table.Column<string>(type: "TEXT", nullable: true),
                    CommitSha = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Subtasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Subtasks_Runs_RunId",
                        column: x => x.RunId,
                        principalTable: "Runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentLogs_RunId",
                table: "AgentLogs",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_Diffs_RunId",
                table: "Diffs",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_Runs_Status",
                table: "Runs",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Runs_TaskId",
                table: "Runs",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_Subtasks_RunId_Order",
                table: "Subtasks",
                columns: new[] { "RunId", "Order" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentLogs");

            migrationBuilder.DropTable(
                name: "Diffs");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "Subtasks");

            migrationBuilder.DropTable(
                name: "Runs");

            migrationBuilder.DropTable(
                name: "Tasks");
        }
    }
}
