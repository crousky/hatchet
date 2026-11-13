using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Factory.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class SecurityGates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PlanReviewByUser",
                table: "Tasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "AgentFixerResets",
                table: "Subtasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "GuidanceFromAgent",
                table: "Subtasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ReviewGapAccepted",
                table: "Subtasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "DetectedVerificationCommand",
                table: "Runs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingVerificationCommand",
                table: "Runs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PlanHeldForUser",
                table: "Runs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PlanReviewByUser",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "AgentFixerResets",
                table: "Subtasks");

            migrationBuilder.DropColumn(
                name: "GuidanceFromAgent",
                table: "Subtasks");

            migrationBuilder.DropColumn(
                name: "ReviewGapAccepted",
                table: "Subtasks");

            migrationBuilder.DropColumn(
                name: "DetectedVerificationCommand",
                table: "Runs");

            migrationBuilder.DropColumn(
                name: "PendingVerificationCommand",
                table: "Runs");

            migrationBuilder.DropColumn(
                name: "PlanHeldForUser",
                table: "Runs");
        }
    }
}
