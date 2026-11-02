using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Factory.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class RunPausedByUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PausedByUser",
                table: "Runs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PausedByUser",
                table: "Runs");
        }
    }
}
