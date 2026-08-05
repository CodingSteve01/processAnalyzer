using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProcessAnalyzer.Web.Data;

#nullable disable

namespace ProcessAnalyzer.Web.Migrations;

/// <summary>
/// Puts the origin of a fact into the activity name, so a case reads as one chain instead of two.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260803140000_AbsenceChain")]
public partial class AbsenceChain : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(Read("013-absence-chain.sql"));

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
