namespace ProcessAnalyzer.Tests;

/// <summary>
/// Reads a vocabulary directory the way <c>VocabularyLoader</c> does, for the tests that check the files themselves.
/// </summary>
internal static class Vocabulary
{
    public record Label(string Kind, string Type, string Text);

    /// <summary>
    /// The catalogue, with a floor on its size so a broken read cannot make a coverage assertion vacuously green.
    /// </summary>
    /// <remarks>
    /// The floor is the point of this method. An empty list satisfies every "nothing is missing" assertion, so a
    /// mistyped path would look exactly like full coverage — the very failure a coverage test exists to rule out,
    /// reappearing inside the test.
    /// </remarks>
    public static List<(string Kind, string Type)> ReadCatalogue(string directory)
    {
        var path = Path.Combine(directory, "source-catalogue.txt");
        var entries = ReadFields(path, expected: 2, separator: ' ')
            .Select(fields => (Kind: fields[0], Type: fields[1]))
            .ToList();

        Assert.True(entries.Count > 15, $"Catalogue looks truncated: only {entries.Count} entries in {path}");
        return entries;
    }

    public static List<Label> ReadLabels(string directory)
    {
        var path = Path.Combine(directory, "labels.tsv");
        var labels = ReadFields(path, expected: 3, separator: '\t')
            .Select(fields => new Label(fields[0], fields[1], fields[2]))
            .ToList();

        Assert.True(labels.Count > 15, $"Labels look truncated: only {labels.Count} rows in {path}");
        return labels;
    }

    private static IEnumerable<string[]> ReadFields(string path, int expected, char separator)
    {
        Assert.True(File.Exists(path), $"Vocabulary file missing: {path}");

        return File.ReadAllLines(path)
            .Where(line => line.Length > 0 && line[0] != '#')
            .Select(line => line.Split(separator, expected))
            .Where(fields => fields.Length >= expected)
            .Select(fields => fields.Select(field => field.Trim()).ToArray());
    }
}
