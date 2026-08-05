using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProcessAnalyzer.Web.Data;

#nullable disable

namespace ProcessAnalyzer.Web.Migrations;

/// <summary>
/// The actor dimension and the German labels — the two things that turn a technically correct analysis into one
/// somebody can read without knowing the codebase.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260802120000_DirectoryAndLabels")]
public partial class DirectoryAndLabels : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(Read("004-directory.sql"));
        migrationBuilder.Sql(Read("005-labels.sql"));
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP VIEW IF EXISTS dim.actor_role");
        migrationBuilder.Sql("DROP VIEW IF EXISTS dim.actor_primary_role");
        migrationBuilder.Sql("DROP SCHEMA IF EXISTS dim CASCADE");
        migrationBuilder.Sql("DROP FUNCTION IF EXISTS analytics.label_activity(text)");
        migrationBuilder.Sql("DROP FUNCTION IF EXISTS analytics.label_object(text)");
        migrationBuilder.Sql("DROP TABLE IF EXISTS ocel.label");
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
