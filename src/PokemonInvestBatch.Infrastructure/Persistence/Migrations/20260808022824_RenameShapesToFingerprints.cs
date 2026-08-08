using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PokemonInvestBatch.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Renames the page-shape archive to the page-fingerprint archive.
    ///
    /// Hand-written on purpose. The scaffolder saw the entity type change name
    /// and produced DropTable("shapes") + CreateTable("fingerprints"), which is
    /// faithful to the model and would have destroyed every archived
    /// fingerprint — the one table whose whole value is that it remembers what
    /// the site used to look like. A rename preserves the rows, and preserves
    /// the grants with them, since PostgreSQL attaches privileges to the object
    /// rather than the name.
    /// </summary>
    public partial class RenameShapesToFingerprints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameTable(
                name: "shapes",
                newName: "fingerprints");

            migrationBuilder.Sql(
                "ALTER TABLE fingerprints RENAME CONSTRAINT pk_shapes TO pk_fingerprints;");

            migrationBuilder.RenameColumn(
                name: "shape_json",
                table: "fingerprints",
                newName: "names");

            migrationBuilder.RenameColumn(
                name: "shape_hash",
                table: "visits",
                newName: "fingerprint_hash");

            migrationBuilder.RenameColumn(
                name: "shape_hash",
                table: "parse_failures",
                newName: "fingerprint_hash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "fingerprint_hash",
                table: "parse_failures",
                newName: "shape_hash");

            migrationBuilder.RenameColumn(
                name: "fingerprint_hash",
                table: "visits",
                newName: "shape_hash");

            migrationBuilder.RenameColumn(
                name: "names",
                table: "fingerprints",
                newName: "shape_json");

            migrationBuilder.Sql(
                "ALTER TABLE fingerprints RENAME CONSTRAINT pk_fingerprints TO pk_shapes;");

            migrationBuilder.RenameTable(
                name: "fingerprints",
                newName: "shapes");
        }
    }
}
