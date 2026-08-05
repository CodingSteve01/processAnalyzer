using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProcessAnalyzer.Web.Data;

#nullable disable

namespace ProcessAnalyzer.Web.Migrations;

/// <summary>
/// Makes the step discriminator a table of rules instead of a chain of CASE branches inside a shipped function.
/// </summary>
/// <remarks>
/// Every family of types that needed its own attribute used to mean editing a function that had already run
/// everywhere. As rows the same rule is configuration, and a source that renames a type is a corrected row rather
/// than a release.
/// </remarks>
[DbContext(typeof(AppDbContext))]
[Migration("20260805060000_DiscriminatorRules")]
public partial class DiscriminatorRules : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(Read("017-discriminator-rules.sql"));

    protected override void Down(MigrationBuilder migrationBuilder) =>
        // The table goes, the function does not: reverting it would leave analytics.activity_of selecting from a
        // table that no longer exists, and every screen would fail rather than lose one discriminator.
        migrationBuilder.Sql(
            """
            CREATE OR REPLACE FUNCTION analytics.activity_of(p_type text, p_attrs jsonb)
            RETURNS text LANGUAGE sql IMMUTABLE AS $$
                SELECT p_type || COALESCE(' [' || COALESCE(
                    p_attrs ->> 'role', p_attrs ->> 'actionType', p_attrs ->> 'action') || ']', '');
            $$;
            DROP TABLE ocel.discriminator_rule;
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
