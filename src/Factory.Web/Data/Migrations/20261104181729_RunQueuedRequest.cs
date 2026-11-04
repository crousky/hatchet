using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Factory.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class RunQueuedRequest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "QueuedRequest",
                table: "Runs",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "QueuedRequest",
                table: "Runs");
        }
    }
}
