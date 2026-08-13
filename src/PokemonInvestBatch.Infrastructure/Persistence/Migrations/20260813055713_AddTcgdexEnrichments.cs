using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PokemonInvestBatch.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTcgdexEnrichments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tcgdex_enrichments",
                columns: table => new
                {
                    card_id = table.Column<long>(type: "bigint", nullable: false),
                    computed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    card_number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    set_official_size = table.Column<int>(type: "integer", nullable: true),
                    tcgdex_set_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    tcgdex_card_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    tcgdex_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    tcgdex_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tcgdex_enrichments", x => new { x.card_id, x.computed_at });
                    table.ForeignKey(
                        name: "fk_tcgdex_enrichments_cards_card_id",
                        column: x => x.card_id,
                        principalTable: "cards",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tcgdex_enrichments");
        }
    }
}
