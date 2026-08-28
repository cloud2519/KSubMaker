using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KSubMaker.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Adds the free-text 메모 the user attaches to a job from the grid. Nullable and additive: an
    /// upgraded database has every existing row's note as NULL, i.e. no note.
    /// </summary>
    public partial class AddJobNote : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Note",
                table: "Jobs",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Note",
                table: "Jobs");
        }
    }
}
