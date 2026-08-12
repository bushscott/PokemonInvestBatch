using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PokemonInvestBatch.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCardNearMissAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "near_miss_at",
                table: "cards",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "near_miss_at",
                table: "cards");
        }
    }
}
