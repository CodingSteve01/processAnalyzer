namespace ProcessAnalyzer.Web.Analytics;

/// <summary>
/// The window a question is asked about.
/// </summary>
/// <remarks>
/// Scopes whole cases: a case counts when it started inside the window, and then all of its events do. Filtering
/// events by date would truncate cases that began earlier, making every duration computed from them wrong rather
/// than partial.
/// <para>
/// An absent bound is NULL, not a sentinel date, so an unfiltered request keeps the same query plan.
/// </para>
/// </remarks>
/// <param name="From">Inclusive lower bound on the case start, or <c>null</c>.</param>
/// <param name="Until">Exclusive upper bound on the case start, or <c>null</c>.</param>
public sealed record Period(DateTimeOffset? From, DateTimeOffset? Until)
{
    /// <summary>No window: every case.</summary>
    public static Period All { get; } = new(null, null);

    /// <summary>
    /// Reads a period off the query string, tolerating an absent or unparsable bound rather than failing.
    /// </summary>
    /// <remarks>
    /// A malformed date widens the window instead of failing: the dashboard cannot render a 400, so the reader would
    /// get an empty page with no reason. The header states the window that was actually applied.
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
