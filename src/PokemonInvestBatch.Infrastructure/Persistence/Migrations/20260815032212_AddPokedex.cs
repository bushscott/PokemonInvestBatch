using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PokemonInvestBatch.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPokedex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "card_tagging",
                columns: table => new
                {
                    card_id = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    method = table.Column<short>(type: "smallint", nullable: false),
                    tagged_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_card_tagging", x => x.card_id);
                    table.ForeignKey(
                        name: "fk_card_tagging_cards_card_id",
                        column: x => x.card_id,
                        principalTable: "cards",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "set_details",
                columns: table => new
                {
                    set_id = table.Column<long>(type: "bigint", nullable: false),
                    match_status = table.Column<short>(type: "smallint", nullable: false),
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    released_on = table.Column<DateOnly>(type: "date", nullable: true),
                    series = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    era = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_set_details", x => x.set_id);
                    table.ForeignKey(
                        name: "fk_set_details_sets_set_id",
                        column: x => x.set_id,
                        principalTable: "sets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "species",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    slug = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    generation = table.Column<short>(type: "smallint", nullable: false),
                    region = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    color = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    habitat = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    stage = table.Column<short>(type: "smallint", nullable: false),
                    evolves_from_species_id = table.Column<int>(type: "integer", nullable: true),
                    gradient_start = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    gradient_end = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_species", x => x.id);
                    table.ForeignKey(
                        name: "fk_species_species_evolves_from_species_id",
                        column: x => x.evolves_from_species_id,
                        principalTable: "species",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "card_species",
                columns: table => new
                {
                    card_id = table.Column<long>(type: "bigint", nullable: false),
                    species_id = table.Column<int>(type: "integer", nullable: false),
                    method = table.Column<short>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_card_species", x => new { x.card_id, x.species_id });
                    table.ForeignKey(
                        name: "fk_card_species_cards_card_id",
                        column: x => x.card_id,
                        principalTable: "cards",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_card_species_species_species_id",
                        column: x => x.species_id,
                        principalTable: "species",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "species_egg_groups",
                columns: table => new
                {
                    species_id = table.Column<int>(type: "integer", nullable: false),
                    egg_group = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_species_egg_groups", x => new { x.species_id, x.egg_group });
                    table.ForeignKey(
                        name: "fk_species_egg_groups_species_species_id",
                        column: x => x.species_id,
                        principalTable: "species",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "species_names",
                columns: table => new
                {
                    species_id = table.Column<int>(type: "integer", nullable: false),
                    language = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_species_names", x => new { x.species_id, x.language });
                    table.ForeignKey(
                        name: "fk_species_names_species_species_id",
                        column: x => x.species_id,
                        principalTable: "species",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "species_types",
                columns: table => new
                {
                    species_id = table.Column<int>(type: "integer", nullable: false),
                    slot = table.Column<short>(type: "smallint", nullable: false),
                    type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_species_types", x => new { x.species_id, x.slot });
                    table.ForeignKey(
                        name: "fk_species_types_species_species_id",
                        column: x => x.species_id,
                        principalTable: "species",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_card_species_species_id_card_id",
                table: "card_species",
                columns: new[] { "species_id", "card_id" });

            migrationBuilder.CreateIndex(
                name: "ix_species_evolves_from_species_id",
                table: "species",
                column: "evolves_from_species_id");

            migrationBuilder.CreateIndex(
                name: "ix_species_slug",
                table: "species",
                column: "slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "card_species");

            migrationBuilder.DropTable(
                name: "card_tagging");

            migrationBuilder.DropTable(
                name: "set_details");

            migrationBuilder.DropTable(
                name: "species_egg_groups");

            migrationBuilder.DropTable(
                name: "species_names");

            migrationBuilder.DropTable(
                name: "species_types");

            migrationBuilder.DropTable(
                name: "species");
        }
    }
}
