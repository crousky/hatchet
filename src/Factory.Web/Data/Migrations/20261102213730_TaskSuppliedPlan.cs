using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Factory.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class TaskSuppliedPlan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SuppliedPlanJson",
                table: "Tasks",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SuppliedPlanJson",
                table: "Tasks");
        }
    }
}
