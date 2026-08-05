using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProcessAnalyzer.Web.Data;

#nullable disable

namespace ProcessAnalyzer.Web.Migrations;

/// <summary>
/// Machines render as their role, never as a pseudonym.
/// </summary>
/// <remarks>
/// A separate migration rather than an edit to the previous one: 007-identity.sql has already run on every database
/// that exists, and changing a shipped migration only changes what a fresh install gets — the running ones keep the
/// old function and quietly disagree with the code.
/// </remarks>
[DbContext(typeof(AppDbContext))]
[Migration("20260802210000_MachinesAsRoles")]
public partial class MachinesAsRoles : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(Read("008-machines-as-roles.sql"));

    protected override void Down(MigrationBuilder migrationBuilder) =>
        // Nothing to undo that matters: the previous definition rendered a pseudonym next to the role for machines,
        // which was noise rather than information.
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
