using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProcessAnalyzer.Web.Data;

#nullable disable

namespace ProcessAnalyzer.Web.Migrations;

/// <summary>
/// Object labels fall back to the entity noun, so an entity that only appears through the generic tier is named on
/// the screens that count objects too.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260806060000_ObjectLabelsGenericTier")]
public partial class ObjectLabelsGenericTier : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(Read("018-object-labels-generic-tier.sql"));

    protected override void Down(MigrationBuilder migrationBuilder) =>
        // Back to the declared-objects-only lookup from 005-labels.sql.
        migrationBuilder.Sql(
            """
            CREATE OR REPLACE FUNCTION analytics.label_object(p_type text)
            RETURNS text LANGUAGE sql STABLE AS $$
                SELECT coalesce((SELECT label_de FROM ocel.label WHERE kind = 'object' AND type_name = p_type),
                                '⚠ ' || p_type);
            $$;
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
