using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcessAnalyzer.Web.Migrations
{
    /// <inheritdoc />
    public partial class DropCausationId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "causation_id", schema: "journal", table: "event");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "causation_id",
                schema: "journal",
                table: "event",
                type: "uuid",
                nullable: true
            );
        }
    }
}
