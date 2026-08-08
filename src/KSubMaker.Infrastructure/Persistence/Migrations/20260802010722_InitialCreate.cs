using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KSubMaker.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppSettings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppSettings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Jobs",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    VideoPath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false, collation: "NOCASE"),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    FileSize = table.Column<long>(type: "INTEGER", nullable: false),
                    LastWriteTimeUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DurationSeconds = table.Column<double>(type: "REAL", nullable: false),
                    HasAudioTrack = table.Column<bool>(type: "INTEGER", nullable: false),
                    HasEmbeddedSubtitle = table.Column<bool>(type: "INTEGER", nullable: false),
                    HasExternalSubtitle = table.Column<bool>(type: "INTEGER", nullable: false),
                    HasKoreanSubtitle = table.Column<bool>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CurrentStage = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    OverallProgress = table.Column<double>(type: "REAL", nullable: false),
                    StageProgress = table.Column<double>(type: "REAL", nullable: false),
                    ProcessingSpeed = table.Column<double>(type: "REAL", nullable: false),
                    DetectedLanguage = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    LanguageProbability = table.Column<double>(type: "REAL", nullable: true),
                    WhisperModel = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    TranslationEngine = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    TranslationModel = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    OutputPath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    ErrorCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    RetryCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    QueueOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Models",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Version = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    LocalPath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    DownloadUrl = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Installed = table.Column<bool>(type: "INTEGER", nullable: false),
                    Verified = table.Column<bool>(type: "INTEGER", nullable: false),
                    RecommendedVramBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    InstalledAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    VerifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DownloadedBytes = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Models", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_QueueOrder",
                table: "Jobs",
                column: "QueueOrder");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_Status",
                table: "Jobs",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_VideoPath",
                table: "Jobs",
                column: "VideoPath");

            migrationBuilder.CreateIndex(
                name: "IX_Models_Type",
                table: "Models",
                column: "Type");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppSettings");

            migrationBuilder.DropTable(
                name: "Jobs");

            migrationBuilder.DropTable(
                name: "Models");
        }
    }
}
