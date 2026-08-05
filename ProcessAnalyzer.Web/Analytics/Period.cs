namespace ProcessAnalyzer.Web.Analytics;

/// <summary>
/// The window a question is asked about.
/// </summary>
/// <remarks>
/// Scopes whole cases: a case counts when it STARTED inside the window, and then all of its events do. Filtering
/// events by date instead would truncate cases that began earlier — their first step would be whatever happened to
/// fall inside the window, and every duration computed from that is wrong rather than partial.
/// <para>
/// Both bounds are optional and an absent bound is not a sentinel date but a real absence: the predicate below tests
/// for NULL, so an unfiltered request produces the same plan it always did.
/// </para>
/// </remarks>
/// <param name="From">Inclusive lower bound on the case start, or <c>null</c>.</param>
/// <param name="Until">Exclusive upper bound on the case start, or <c>null</c>.</param>
public sealed record Period(DateTimeOffset? From, DateTimeOffset? Until)
{
    /// <summary>No window — every case.</summary>
    public static Period All { get; } = new(null, null);

    /// <summary>
    /// Reads a period off the query string, tolerating an absent or unparsable bound rather than failing.
    /// </summary>
    /// <remarks>
    /// A malformed date silently widening the window is the lesser evil here: the alternative is a 400 on a dashboard
    /// that has no way to show it, and the reader would see an empty page with no reason given. The rendered header
    /// states the window that was actually applied.
    /// </remarks>
    public static Period FromQuery(string? from, string? until) => new(Parse(from), Parse(until));

    /// <summary>The two parameters the predicate expects. Always bound, so the SQL never varies by request.</summary>
    public (string Name, object Value)[] Parameters() =>
        [
            ("periodFrom", From.HasValue ? From.Value.UtcDateTime : DBNull.Value),
            ("periodUntil", Until.HasValue ? Until.Value.UtcDateTime : DBNull.Value),
        ];

    private static DateTimeOffset? Parse(string? value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
}
