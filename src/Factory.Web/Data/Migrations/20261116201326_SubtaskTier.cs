using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Factory.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class SubtaskTier : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Tier",
                table: "Subtasks",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Tier",
                table: "Subtasks");
        }
    }
}
