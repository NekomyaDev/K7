using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace K7.Server.Infrastructure.Database.Providers.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddIndexedFileIdentificationExpectedLayout : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Identification_ExpectedDiscCount",
                table: "IndexedFiles",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Identification_ExpectedTrackCount",
                table: "IndexedFiles",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Identification_ExpectedDiscCount",
                table: "IndexedFiles");

            migrationBuilder.DropColumn(
                name: "Identification_ExpectedTrackCount",
                table: "IndexedFiles");
        }
    }
}
