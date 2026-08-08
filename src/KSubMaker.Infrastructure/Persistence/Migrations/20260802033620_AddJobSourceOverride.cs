using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KSubMaker.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Adds the per-file 자막 원본 override: which source this one video should be translated from
    /// (audio vs. a specific embedded subtitle track) and the language the user confirmed for it.
    ///
    /// Additive only. Every existing row is backfilled with <c>None</c>, i.e. the MVP core path, so an
    /// upgraded database behaves exactly as it did before the user touches anything.
    /// </summary>
    public partial class AddJobSourceOverride : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SelectedAudioTrackIndex",
                table: "Jobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SelectedSubtitleLanguage",
                table: "Jobs",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SelectedSubtitleTrackIndex",
                table: "Jobs",
                type: "INTEGER",
                nullable: true);

            // "None", not the scaffolder's "": the column stores the enum by *name*, and an empty
            // string would make every pre-existing row unreadable the moment EF tried to parse it.
            migrationBuilder.AddColumn<string>(
                name: "SourceOverride",
                table: "Jobs",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "None");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SelectedAudioTrackIndex",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "SelectedSubtitleLanguage",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "SelectedSubtitleTrackIndex",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "SourceOverride",
                table: "Jobs");
        }
    }
}
