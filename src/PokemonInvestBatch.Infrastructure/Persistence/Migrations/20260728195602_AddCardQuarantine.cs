using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PokemonInvestBatch.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCardQuarantine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "failure_streak",
                table: "cards",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "quarantined_until",
                table: "cards",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "failure_streak",
                table: "cards");

            migrationBuilder.DropColumn(
                name: "quarantined_until",
                table: "cards");
        }
    }
}
