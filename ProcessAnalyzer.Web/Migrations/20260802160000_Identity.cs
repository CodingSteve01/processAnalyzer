using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProcessAnalyzer.Web.Data;

#nullable disable

namespace ProcessAnalyzer.Web.Migrations;

/// <summary>The identity switch: pseudonyms by default, names when the operator turns them on.</summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260802160000_Identity")]
public partial class Identity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Read("007-identity.sql"));

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP FUNCTION IF EXISTS analytics.person_with_role(text)");
        migrationBuilder.Sql("DROP FUNCTION IF EXISTS analytics.person(text)");
        migrationBuilder.Sql("DROP FUNCTION IF EXISTS analytics.show_identity()");
        migrationBuilder.Sql("DROP TABLE IF EXISTS analytics.setting");
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
