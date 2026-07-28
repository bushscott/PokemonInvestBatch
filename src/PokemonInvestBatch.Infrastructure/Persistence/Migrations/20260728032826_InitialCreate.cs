using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PokemonInvestBatch.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "parse_failures",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    shape_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_parse_failures", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sets",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    slug = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    discovered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sets", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "shapes",
                columns: table => new
                {
                    hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    shape_json = table.Column<string>(type: "jsonb", nullable: false),
                    sample_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shapes", x => x.hash);
                });

            migrationBuilder.CreateTable(
                name: "visits",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    kind = table.Column<short>(type: "smallint", nullable: false),
                    url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    card_id = table.Column<long>(type: "bigint", nullable: true),
                    fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    http_status = table.Column<int>(type: "integer", nullable: false),
                    outcome = table.Column<short>(type: "smallint", nullable: false),
                    shape_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_visits", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "cards",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false),
                    set_id = table.Column<long>(type: "bigint", nullable: false),
                    url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    image_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    image_fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_visited_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    observed_sales_per_day = table.Column<double>(type: "double precision", nullable: true),
                    any_bucket_at_cap = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cards", x => x.id);
                    table.ForeignKey(
                        name: "fk_cards_sets_set_id",
                        column: x => x.set_id,
                        principalTable: "sets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "populations",
                columns: table => new
                {
                    card_id = table.Column<long>(type: "bigint", nullable: false),
                    grader = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    grade = table.Column<short>(type: "smallint", nullable: false),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    population = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_populations", x => new { x.card_id, x.grader, x.grade, x.observed_at });
                    table.ForeignKey(
                        name: "fk_populations_cards_card_id",
                        column: x => x.card_id,
                        principalTable: "cards",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "price_months",
                columns: table => new
                {
                    card_id = table.Column<long>(type: "bigint", nullable: false),
                    tier = table.Column<int>(type: "integer", nullable: false),
                    month = table.Column<DateOnly>(type: "date", nullable: false),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    price_cents = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_price_months", x => new { x.card_id, x.tier, x.month, x.observed_at });
                    table.ForeignKey(
                        name: "fk_price_months_cards_card_id",
                        column: x => x.card_id,
                        principalTable: "cards",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sales",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    card_id = table.Column<long>(type: "bigint", nullable: false),
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    source_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    sold_on = table.Column<DateOnly>(type: "date", nullable: false),
                    grade_tier = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    price_cents = table.Column<int>(type: "integer", nullable: false),
                    listed_price_cents = table.Column<int>(type: "integer", nullable: true),
                    title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    captured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sales", x => x.id);
                    table.ForeignKey(
                        name: "fk_sales_cards_card_id",
                        column: x => x.card_id,
                        principalTable: "cards",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_cards_last_visited_at",
                table: "cards",
                column: "last_visited_at");

            migrationBuilder.CreateIndex(
                name: "ix_cards_set_id",
                table: "cards",
                column: "set_id");

            migrationBuilder.CreateIndex(
                name: "ix_parse_failures_fetched_at",
                table: "parse_failures",
                column: "fetched_at");

            migrationBuilder.CreateIndex(
                name: "ix_sales_card_id_sold_on",
                table: "sales",
                columns: new[] { "card_id", "sold_on" });

            migrationBuilder.CreateIndex(
                name: "ix_sales_source_source_id",
                table: "sales",
                columns: new[] { "source", "source_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sets_slug",
                table: "sets",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_visits_fetched_at",
                table: "visits",
                column: "fetched_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "parse_failures");

            migrationBuilder.DropTable(
                name: "populations");

            migrationBuilder.DropTable(
                name: "price_months");

            migrationBuilder.DropTable(
                name: "sales");

            migrationBuilder.DropTable(
                name: "shapes");

            migrationBuilder.DropTable(
                name: "visits");

            migrationBuilder.DropTable(
                name: "cards");

            migrationBuilder.DropTable(
                name: "sets");
        }
    }
}
