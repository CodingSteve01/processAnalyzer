using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProcessAnalyzer.Web.Data;

#nullable disable

namespace ProcessAnalyzer.Web.Migrations;

/// <summary>
/// Who somebody IS, from the views they set up for themselves.
/// </summary>
/// <remarks>
/// The log says who did what and never who they are. Many people work in the same module and do completely different
/// things with it, so every figure grouped by module was an average over roles that have nothing to do with each other —
/// and the group directory cannot fix it, because a department is not a role. Saved views can: what somebody configured
/// for themselves is dated, specific, and already written down.
/// </remarks>
[DbContext(typeof(AppDbContext))]
[Migration("20260807140000_SavedViews")]
public partial class SavedViews : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Read("029-saved-views.sql"));

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            DROP VIEW IF EXISTS analytics.view_population;
            DROP VIEW IF EXISTS analytics.filter_vocabulary;
            DROP VIEW IF EXISTS analytics.view_profile;
            DROP TABLE IF EXISTS dim.saved_view_column;
            DROP TABLE IF EXISTS dim.saved_view_filter;
            DROP TABLE IF EXISTS dim.saved_view;
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
