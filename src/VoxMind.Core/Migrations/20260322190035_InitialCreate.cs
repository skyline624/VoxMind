using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VoxMind.Core.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ListeningSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ParticipantsJson = table.Column<string>(type: "TEXT", nullable: true),
                    ConfigJson = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ListeningSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SpeakerProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    AliasesJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DetectionCount = table.Column<int>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpeakerProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SessionSegments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SpeakerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    StartTime = table.Column<double>(type: "REAL", nullable: false),
                    EndTime = table.Column<double>(type: "REAL", nullable: false),
                    Transcript = table.Column<string>(type: "TEXT", nullable: false),
                    Confidence = table.Column<float>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionSegments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SessionSegments_ListeningSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "ListeningSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SessionSummaries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    GeneratedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FullTranscript = table.Column<string>(type: "TEXT", nullable: true),
                    KeyMomentsJson = table.Column<string>(type: "TEXT", nullable: true),
                    DecisionsJson = table.Column<string>(type: "TEXT", nullable: true),
                    ActionItemsJson = table.Column<string>(type: "TEXT", nullable: true),
                    GeneratedSummary = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionSummaries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SessionSummaries_ListeningSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "ListeningSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SpeakerEmbeddings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FilePath = table.Column<string>(type: "TEXT", nullable: false),
                    CapturedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    InitialConfidence = table.Column<float>(type: "REAL", nullable: false),
                    AudioDurationSeconds = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpeakerEmbeddings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SpeakerEmbeddings_SpeakerProfiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "SpeakerProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_sessions_started",
                table: "ListeningSessions",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "idx_segments_session",
                table: "SessionSegments",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_SessionSummaries_SessionId",
                table: "SessionSummaries",
                column: "SessionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_embeddings_profile",
                table: "SpeakerEmbeddings",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "idx_profiles_lastseen",
                table: "SpeakerProfiles",
                column: "LastSeenAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SessionSegments");

            migrationBuilder.DropTable(
                name: "SessionSummaries");

            migrationBuilder.DropTable(
                name: "SpeakerEmbeddings");

            migrationBuilder.DropTable(
                name: "ListeningSessions");

            migrationBuilder.DropTable(
                name: "SpeakerProfiles");
        }
    }
}
