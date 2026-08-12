using Xunit;

namespace ProcessAnalyzer.Tests;

/// <summary>
/// Every type a source can emit has a German label before it ever reaches a screen.
/// </summary>
/// <remarks>
/// This exists because the first label set was written against the demo seed rather than against the catalogue, and
/// named several types only the seed produced, the same fact under a slightly different name. Against demo data every
/// screen looked finished; against a real journal most steps would have rendered as raw dotted identifiers, and nobody
/// would have noticed until the first person opened the dashboard and found a wall of dots.
///
/// A missing label is not a cosmetic defect: an unlabelled activity is also an unexplained one, and the tool's entire
/// claim is that somebody who has never seen the process can read what happened.
///
/// These are file checks, not database checks. Coverage is a question about two files, the catalogue and the labels,
/// so answering it with a container proved the same thing more slowly, and only for the one vocabulary that happened
/// to be loaded. Now every vocabulary in the checkout is checked, and the rendering rules are tested separately in
/// <see cref="AnalyticsSqlTests"/>.
/// </remarks>
public sealed class LabelCoverageTests
{
    [Theory]
    [MemberData(nameof(Vocabularies))]
    public void EveryDeclaredTypeHasALabel(string directory)
    {
        var catalogue = Vocabulary.ReadCatalogue(directory);
        var labels = Vocabulary.ReadLabels(directory);

        // 'entity' is the generic tier: one noun per entity renders all four verbs, so the entity is what needs a word.
        var missing = catalogue
            .Where(entry => !labels.Any(label => label.Kind == entry.Kind && label.Type == entry.Type))
            .Select(entry => $"{entry.Kind} {entry.Type}")
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"Without a German label in {directory} ({missing.Count}):{Environment.NewLine}"
                + string.Join(Environment.NewLine, missing)
        );
    }

    [Theory]
    [MemberData(nameof(Vocabularies))]
    public void NoLabelIsACopyOfTheTechnicalKey(string directory)
    {
        // A label that still contains a dotted identifier is a copy of the key, not a translation of it: the exact
        // failure this whole file guards against, and cheap to catch mechanically.
        var offenders = Vocabulary
            .ReadLabels(directory)
            .Where(label =>
                label.Text == label.Type || label.Text.EndsWith(".v1", StringComparison.Ordinal) || IsDotted(label.Text)
            )
            .Select(label => $"{label.Kind} {label.Type} → {label.Text}")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"Technical key used as a label in {directory}:{Environment.NewLine}"
                + string.Join(Environment.NewLine, offenders)
        );
    }

    [Theory]
    [MemberData(nameof(Vocabularies))]
    public void NoTwoStepsShareALabel(string directory)
    {
        // Two event types under one label merge two steps into a single line on every screen, the same damage as a
        // collapsed activity label, only harder to spot because the line reads perfectly well.
        var duplicates = Vocabulary
            .ReadLabels(directory)
            .Where(label => label.Kind == "event")
            .GroupBy(label => label.Text)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key} ← {string.Join(", ", group.Select(l => l.Type).Order())}")
            .ToList();

        Assert.True(
            duplicates.Count == 0,
            $"Two steps sharing one label in {directory}:{Environment.NewLine}"
                + string.Join(Environment.NewLine, duplicates)
        );
    }

    [Theory]
    [MemberData(nameof(Vocabularies))]
    public void EveryVerbOfTheGenericTierHasAWord(string directory)
    {
        // The four verbs are closed by the producer's type pattern. A missing one shows up on screen as
        // "Beleg updated", which reads like a bug in the label rather than a gap in the vocabulary.
        var verbs = Vocabulary
            .ReadLabels(directory)
            .Where(label => label.Kind == "verb")
            .Select(label => label.Type)
            .ToHashSet();

        Assert.Equal<string[]>(["copied", "created", "deleted", "updated"], verbs.Order().ToArray());
    }

    /// <summary>
    /// Every vocabulary in the checkout: the examples always, an installation's own when it is there.
    /// </summary>
    /// <remarks>
    /// A MemberData that could return nothing would make the suite vacuously green, which is the failure a coverage
    /// test exists to rule out. The examples are committed, so there is always at least one case.
    /// </remarks>
    public static TheoryData<string> Vocabularies()
    {
        var data = new TheoryData<string> { TestVocabulary.ExampleDirectory };
        if (TestVocabulary.OwnDirectory is { } own)
            data.Add(own);

        return data;
    }

    private static bool IsDotted(string label) =>
        label.Contains('.', StringComparison.Ordinal) && !label.Contains(' ', StringComparison.Ordinal);
}
