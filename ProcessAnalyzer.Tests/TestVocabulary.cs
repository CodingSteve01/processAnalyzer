namespace ProcessAnalyzer.Tests;

/// <summary>
/// The two vocabularies the suite runs against: the committed examples, always, and an installation's own when the
/// checkout happens to have one.
/// </summary>
/// <remarks>
/// The split is what makes the label tests deterministic. Asserting a rendered German sentence needs known input, so
/// those tests use the examples and pass on any machine. Asserting that every declared type has a word needs the real
/// catalogue, and that assertion is simply stronger where a real vocabulary exists.
///
/// Both are loaded, not one or the other: a suite that only checked coverage when a real vocabulary happened to be
/// present would report green on a machine without one, and a checker that has gone blind reports green forever.
/// </remarks>
internal static class TestVocabulary
{
    /// <summary>The committed examples. Always present, so every rendering rule is exercised everywhere.</summary>
    public static string ExampleDirectory => Find("vocabulary.example");

    /// <summary>An installation's own vocabulary, or null when the checkout has none.</summary>
    public static string? OwnDirectory
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("PA_VOCABULARY_PATH");
            if (!string.IsNullOrWhiteSpace(configured))
                return Directory.Exists(configured) ? configured : null;

            try
            {
                return Find("vocabulary");
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    /// <summary>The catalogue in use, and whether it is an installation's rather than the example.</summary>
    public static (string Path, bool IsOwn) Catalogue()
    {
        var own = OwnDirectory is null ? null : Path.Combine(OwnDirectory, "source-catalogue.txt");
        return own is not null && File.Exists(own)
            ? (own, true)
            : (Path.Combine(ExampleDirectory, "source-catalogue.txt"), false);
    }

    private static string Find(string name)
    {
        var probe = new DirectoryInfo(AppContext.BaseDirectory);
        while (probe is not null)
        {
            var candidate = Path.Combine(probe.FullName, name);
            if (Directory.Exists(candidate))
                return candidate;

            probe = probe.Parent;
        }

        throw new InvalidOperationException($"No '{name}' directory found above {AppContext.BaseDirectory}.");
    }
}
