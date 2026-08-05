using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ProcessAnalyzer.Web.Migrations
{
    /// <inheritdoc />
    public partial class InitialJournal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(name: "sync");

            migrationBuilder.EnsureSchema(name: "journal");

            migrationBuilder.CreateTable(
                name: "cursor",
                schema: "sync",
                columns: table => new
                {
                    name = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cursor", x => x.name);
                }
            );

            migrationBuilder.CreateTable(
                name: "event",
                schema: "journal",
                columns: table => new
                {
                    source_id = table.Column<long>(type: "bigint", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    recorded_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    performer_type = table.Column<string>(type: "text", nullable: false),
                    performer_id = table.Column<string>(type: "text", nullable: true),
                    initiator_type = table.Column<string>(type: "text", nullable: true),
                    initiator_id = table.Column<string>(type: "text", nullable: true),
                    correlation_id = table.Column<string>(type: "text", nullable: true),
                    causation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    trace_id = table.Column<string>(type: "text", nullable: true),
                    source_application = table.Column<string>(type: "text", nullable: false),
                    source_module = table.Column<string>(type: "text", nullable: true),
                    source_version = table.Column<string>(type: "text", nullable: true),
                    payload = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    payload_raw = table.Column<string>(type: "text", nullable: true),
                    mandate_id = table.Column<long>(type: "bigint", nullable: true),
                    pulled_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    projection_version = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_event", x => x.source_id);
                }
            );

            migrationBuilder.CreateTable(
                name: "run",
                schema: "sync",
                columns: table => new
                {
                    id = table
                        .Column<long>(type: "bigint", nullable: false)
                        .Annotation(
                            "Npgsql:ValueGenerationStrategy",
                            NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
                        ),
                    kind = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    finished_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    from_id = table.Column<long>(type: "bigint", nullable: true),
                    to_id = table.Column<long>(type: "bigint", nullable: true),
                    events = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    objects = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    held_back = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    gaps_found = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    elapsed_ms = table.Column<int>(type: "integer", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_run", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "event_object",
                schema: "journal",
                columns: table => new
                {
                    source_id = table.Column<long>(type: "bigint", nullable: false),
                    event_source_id = table.Column<long>(type: "bigint", nullable: false),
                    object_type = table.Column<string>(type: "text", nullable: false),
                    object_id = table.Column<string>(type: "text", nullable: false),
                    qualifier = table.Column<string>(type: "text", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_event_object", x => x.source_id);
                    table.ForeignKey(
                        name: "FK_event_object_event_event_source_id",
                        column: x => x.event_source_id,
                        principalSchema: "journal",
                        principalTable: "event",
                        principalColumn: "source_id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "ix_journal_event_corr",
                schema: "journal",
                table: "event",
                column: "correlation_id",
                filter: "\"correlation_id\" IS NOT NULL"
            );

            migrationBuilder.CreateIndex(
                name: "ix_journal_event_event_id",
                schema: "journal",
                table: "event",
                column: "event_id",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_journal_event_recorded",
                schema: "journal",
                table: "event",
                column: "recorded_at"
            );

            migrationBuilder.CreateIndex(
                name: "ix_journal_event_type_time",
                schema: "journal",
                table: "event",
                columns: new[] { "event_type", "occurred_at" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_journal_event_unproj",
                schema: "journal",
                table: "event",
                column: "source_id",
                filter: "\"projection_version\" = 0"
            );

            migrationBuilder.CreateIndex(
                name: "ix_journal_eo_event",
                schema: "journal",
                table: "event_object",
                column: "event_source_id"
            );

            migrationBuilder.CreateIndex(
                name: "ix_journal_eo_object",
                schema: "journal",
                table: "event_object",
                columns: new[] { "object_type", "object_id" }
            );

            migrationBuilder.CreateIndex(
                name: "ux_journal_eo_natural",
                schema: "journal",
                table: "event_object",
                columns: new[] { "event_source_id", "object_type", "object_id", "qualifier" },
                unique: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "cursor", schema: "sync");

            migrationBuilder.DropTable(name: "event_object", schema: "journal");

            migrationBuilder.DropTable(name: "run", schema: "sync");

            migrationBuilder.DropTable(name: "event", schema: "journal");
        }
    }
}
