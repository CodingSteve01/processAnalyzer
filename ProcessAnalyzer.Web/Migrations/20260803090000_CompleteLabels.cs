using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProcessAnalyzer.Web.Data;

#nullable disable

namespace ProcessAnalyzer.Web.Migrations;

/// <summary>
/// German labels for every type the source declares, rather than the handful the demo seed happened to use.
/// </summary>
/// <remarks>
/// Now without statements: the labels are configuration and are loaded at startup from the vocabulary. The file it
/// runs is kept so this migration still applies on a database that has not caught up.
/// </remarks>
[DbContext(typeof(AppDbContext))]
[Migration("20260803090000_CompleteLabels")]
public partial class CompleteLabels : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(Read("010-labels-complete.sql"));

    protected override void Down(MigrationBuilder migrationBuilder) =>
        // Deliberately not a delete: rolling back to raw dotted identifiers on every screen is worse than keeping
        // labels for types the previous version never displayed, and no number depends on the wording.
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
