using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProcessAnalyzer.Web.Data;

#nullable disable

namespace ProcessAnalyzer.Web.Migrations;

/// <summary>
/// Each process is measured by its own clock. Office hours for office work, round the clock for work that runs at
/// night — against one office calendar every duration of an operational process came out as zero.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260806150000_ProcessClock")]
public partial class ProcessClock : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(Read("023-process-clock.sql"));

    protected override void Down(MigrationBuilder migrationBuilder) =>
        // The column goes; the lifecycle view stays as it is. Reverting it would mean re-emitting the whole definition
        // for a rollback nobody wants — and a view that reports business time under a column called duration_seconds is
        // still readable, which a missing column is not.
        migrationBuilder.Sql(
            """
            DROP FUNCTION IF EXISTS analytics.duration_seconds(text, timestamptz, timestamptz);
            DROP VIEW IF EXISTS analytics.process_clock;
            ALTER TABLE analytics.process_closure DROP COLUMN IF EXISTS use_business_hours;
            """
        );

    private static string Read(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly
            .GetManifestResourceNames()
            .Single(candidate => candidate.EndsWith(name, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
