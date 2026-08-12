using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using ProcessAnalyzer.Web.Data;
using ProcessAnalyzer.Web.Options;

namespace ProcessAnalyzer.Web.Vocabulary;

/// <summary>
/// Loads the installation's vocabulary into <c>ocel.label</c>, <c>ocel.discriminator_rule</c> and
/// <c>ocel.payload_allowlist</c>: the German label of every type, which attribute names a step, and the payload
/// attributes the source carries.
/// </summary>
/// <remarks>
/// Which types exist and what they are called belongs to the installation, not the tool: two sources declare
/// different types, and two organisations name the same type differently. The rendering rules therefore stay in SQL
/// (<c>analytics.label_activity</c> and friends) while the wording arrives from
/// <see cref="ProcessAnalyzerOptions.VocabularyPath"/> and can be corrected without a release.
///
/// Upsert, not replace: an installation that ran the older migrations already holds these rows, and deleting first
/// would blank every screen until somebody noticed the missing file.
/// </remarks>
public sealed class VocabularyLoader
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly ProcessAnalyzerOptions _options;
    private readonly ILogger<VocabularyLoader> _log;

    public VocabularyLoader(
        IDbContextFactory<AppDbContext> factory,
        ProcessAnalyzerOptions options,
        ILogger<VocabularyLoader> log
    )
    {
        _factory = factory;
        _options = options;
        _log = log;
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var directory = _options.VocabularyPath;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            // Not fatal, but not silent: without labels every screen renders dotted identifiers behind a warning
            // sign, and an operator has to be able to recognise that state from the log.
            _log.LogWarning(
                "No vocabulary directory at '{Directory}' — labels stay as they are in the database. A fresh "
                    + "installation will render technical type names until labels.tsv is provided.",
                directory
            );
            return;
        }

        await using var db = await _factory.CreateDbContextAsync(ct);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(ct);

        var labels = await LoadLabelsAsync(connection, Path.Combine(directory, "labels.tsv"), ct);
        var allowed = await LoadAllowlistAsync(connection, Path.Combine(directory, "payload-allowlist.tsv"), ct);
        var rules = await LoadDiscriminatorRulesAsync(
            connection,
            Path.Combine(directory, "discriminator-rules.tsv"),
            ct
        );

        _log.LogInformation(
            "Vocabulary loaded: {Labels} labels, {Attributes} payload attributes, {Rules} discriminator rules.",
            labels,
            allowed,
            rules
        );
    }

    private async Task<int> LoadDiscriminatorRulesAsync(NpgsqlConnection connection, string path, CancellationToken ct)
    {
        var rows = ReadRows(path, expected: 3).ToList();
        if (rows.Count == 0)
            return 0;

        var loaded = 0;
        foreach (var fields in rows)
        {
            if (!int.TryParse(fields[0], out var priority))
            {
                _log.LogWarning("{Path}: '{Value}' is not a priority — rule skipped.", path, fields[0]);
                continue;
            }

            await using var command = new NpgsqlCommand(
                "INSERT INTO ocel.discriminator_rule (priority, type_match, attr_name) "
                    + "VALUES (@priority, @match, @attr) ON CONFLICT DO NOTHING",
                connection
            );
            command.Parameters.AddWithValue("priority", priority);
            command.Parameters.AddWithValue("match", fields[1]);
            command.Parameters.AddWithValue("attr", fields[2]);

            await command.ExecuteNonQueryAsync(ct);
            loaded++;
        }

        return loaded;
    }

    private async Task<int> LoadLabelsAsync(NpgsqlConnection connection, string path, CancellationToken ct)
    {
        var rows = ReadRows(path, expected: 3).ToList();
        if (rows.Count == 0)
            return 0;

        var loaded = 0;
        foreach (var fields in rows)
        {
            await using var command = new NpgsqlCommand(
                """
                -- The file value is always remembered, even when a person has overridden the name here: it is what
                -- "back to the vocabulary" restores, and it lets a reader see that this name was changed in the tool.
                --
                -- The visible label is only overwritten for rows the file owns. Overwriting a typed correction on every
                -- restart would undo somebody's work silently, and nobody would connect the restart to the lost word.
                INSERT INTO ocel.label (kind, type_name, label_de, hint_de, source, file_label_de, file_hint_de)
                VALUES (@kind, @type, @label, @hint, 'file', @label, @hint)
                ON CONFLICT (kind, type_name) DO UPDATE
                    SET label_de = CASE WHEN ocel.label.source = 'file' THEN EXCLUDED.label_de ELSE ocel.label.label_de END,
                        hint_de  = CASE WHEN ocel.label.source = 'file' THEN EXCLUDED.hint_de  ELSE ocel.label.hint_de  END,
                        file_label_de = EXCLUDED.label_de,
                        file_hint_de  = EXCLUDED.hint_de
                """,
                connection
            );
            command.Parameters.AddWithValue("kind", fields[0]);
            command.Parameters.AddWithValue("type", fields[1]);
            command.Parameters.AddWithValue("label", fields[2]);
            // An empty fourth column is a NULL hint, not an empty sentence: the screens test the hint for null to
            // decide whether to render the explanation at all.
            command.Parameters.Add(
                new NpgsqlParameter("hint", NpgsqlDbType.Text)
                {
                    Value = fields.Count > 3 && fields[3].Length > 0 ? fields[3] : DBNull.Value,
                }
            );

            await command.ExecuteNonQueryAsync(ct);
            loaded++;
        }

        return loaded;
    }

    private async Task<int> LoadAllowlistAsync(NpgsqlConnection connection, string path, CancellationToken ct)
    {
        var rows = ReadRows(path, expected: 2).ToList();
        if (rows.Count == 0)
            return 0;

        var loaded = 0;
        foreach (var fields in rows)
        {
            await using var command = new NpgsqlCommand(
                "INSERT INTO ocel.payload_allowlist (event_type, attr_name) VALUES (@type, @attr) "
                    + "ON CONFLICT (event_type, attr_name) DO NOTHING",
                connection
            );
            command.Parameters.AddWithValue("type", fields[0]);
            command.Parameters.AddWithValue("attr", fields[1]);

            await command.ExecuteNonQueryAsync(ct);
            loaded++;
        }

        return loaded;
    }

    /// <summary>
    /// Tab-separated, '#' starts a comment. A short line is skipped with a warning rather than throwing: one
    /// malformed row must not cost the other three hundred their labels.
    /// </summary>
    private IEnumerable<List<string>> ReadRows(string path, int expected)
    {
        if (!File.Exists(path))
        {
            _log.LogWarning("Vocabulary file missing: {Path}", path);
            yield break;
        }

        var lineNumber = 0;
        foreach (var line in File.ReadLines(path))
        {
            lineNumber++;
            if (line.Length == 0 || line[0] == '#')
                continue;

            var fields = line.Split('\t').ToList();
            if (fields.Count < expected || fields.Take(expected).Any(string.IsNullOrWhiteSpace))
            {
                _log.LogWarning(
                    "{Path}:{Line} has {Count} usable fields, need {Expected} — skipped.",
                    path,
                    lineNumber,
                    fields.Count,
                    expected
                );
                continue;
            }

            yield return fields;
        }
    }
}
