using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProcessAnalyzer.Web.Data;

#nullable disable

namespace ProcessAnalyzer.Web.Migrations;

/// <summary>
/// Distinguishes the two shapes of one event type in the activity name, so a case does not break in half.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260803160000_TourChain")]
public partial class TourChain : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Read("014-tour-chain.sql"));

    protected override void Down(MigrationBuilder migrationBuilder) =>
        // Not reverted: a leave would read as two unconnected cases again.
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
