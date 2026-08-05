using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProcessAnalyzer.Web.Data;

#nullable disable

namespace ProcessAnalyzer.Web.Migrations;

/// <summary>
/// The business calendar in the shape a source keeps it: hours per weekday, holidays that can be half days.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260802140000_Calendar")]
public partial class Calendar : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Read("006-calendar.sql"));

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP VIEW IF EXISTS analytics.calendar_summary");
        migrationBuilder.Sql("ALTER TABLE analytics.holiday DROP COLUMN IF EXISTS factor");
        migrationBuilder.Sql("ALTER TABLE analytics.holiday DROP COLUMN IF EXISTS source");
        migrationBuilder.Sql("ALTER TABLE analytics.business_slot DROP COLUMN IF EXISTS hours");
        migrationBuilder.Sql("ALTER TABLE analytics.business_slot DROP COLUMN IF EXISTS source");
    }

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
