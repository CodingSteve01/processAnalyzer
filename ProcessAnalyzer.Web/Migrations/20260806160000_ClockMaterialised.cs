using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProcessAnalyzer.Web.Data;

#nullable disable

namespace ProcessAnalyzer.Web.Migrations;

/// <summary>
/// The clock and the end steps become stored answers. As views they were recomputed over the whole log for every row
/// that asked for a duration, and a page that had answered in milliseconds took two minutes.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260806160000_ClockMaterialised")]
public partial class ClockMaterialised : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(Read("024-clock-materialised.sql"));

    protected override void Down(MigrationBuilder migrationBuilder) =>
        // Not reverted: going back would restore a shape in which one request scans the whole log per row.
        migrationBuilder.Sql("-- no-op");

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
