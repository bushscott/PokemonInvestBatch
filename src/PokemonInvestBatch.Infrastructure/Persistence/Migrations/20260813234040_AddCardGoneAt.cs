using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PokemonInvestBatch.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCardGoneAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "gone_at",
                table: "cards",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_cards_gone_at",
                table: "cards",
                column: "gone_at",
                filter: "gone_at IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_cards_gone_at",
                table: "cards");

            migrationBuilder.DropColumn(
                name: "gone_at",
                table: "cards");
        }
    }
}
