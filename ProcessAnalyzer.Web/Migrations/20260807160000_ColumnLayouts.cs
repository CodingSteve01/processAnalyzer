using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProcessAnalyzer.Web.Data;

#nullable disable

namespace ProcessAnalyzer.Web.Migrations;

/// <summary>
/// Which column layouts exist per screen, and who shares one.
/// </summary>
/// <remarks>
/// The question a user-interface rebuild has to answer first. Not which columns exist — the schema says that — but which
/// combinations people actually put on screen, and whether a screen serves one layout or fifteen.
/// </remarks>
[DbContext(typeof(AppDbContext))]
[Migration("20260807160000_ColumnLayouts")]
public partial class ColumnLayouts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(Read("030-column-layouts.sql"));

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            DROP VIEW IF EXISTS analytics.layout_sharing;
            DROP VIEW IF EXISTS analytics.column_layout;
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
