using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProcessAnalyzer.Web.Data;

namespace ProcessAnalyzer.Web.Analytics;

/// <summary>
/// Runs an analytical statement and returns rows as dictionaries.
/// </summary>
/// <remarks>
/// Column names are the contract with the frontend, so the queries name their columns in the language they are
/// displayed in. Mapping each result set to a record type would add a type per question and change nothing about
/// what reaches the browser.
/// </remarks>
internal static class Query
{
    public static async Task<List<Dictionary<string, object?>>> RunAsync(
        IDbContextFactory<AppDbContext> factory,
        string sql,
        CancellationToken ct,
        params (string Name, object Value)[] parameters
    )
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 120;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);

        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i);

            rows.Add(row);
        }

        return rows;
    }
}
