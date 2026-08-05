using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProcessAnalyzer.Web.Data;

#nullable disable

namespace ProcessAnalyzer.Web.Migrations;

/// <summary>
/// The generic tier renders by rule: German noun per entity, German word per verb.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260803120000_GenericActs")]
public partial class GenericActs : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Read("012-generic-acts.sql"));

    protected override void Down(MigrationBuilder migrationBuilder) =>
        // Not reverted: without the rule, two thirds of every journal reads as dotted identifiers.
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
