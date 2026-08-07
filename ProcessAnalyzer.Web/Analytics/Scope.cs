namespace ProcessAnalyzer.Web.Analytics;

/// <summary>
/// What a question is asked about: a window in time and, optionally, one group of people.
/// </summary>
/// <remarks>
/// One value rather than two parameters threaded separately. A panel that took the window but forgot the group would
/// show a number the reader believes is filtered, and nothing fails when a filter is merely absent — the figure just
/// answers a different question than the control above it claims.
/// <para>
/// Both dimensions scope whole CASES. Dropping single events instead would leave a case whose first step is whatever
/// survived the filter, and every duration computed from that is wrong rather than partial.
/// </para>
/// </remarks>
/// <param name="Period">The window on the case start.</param>
/// <param name="Group">A group name from the source directory, or <c>null</c> for everybody.</param>
/// <param name="HasStep">Only cases that went through this step at some point, or <c>null</c>.</param>
/// <param name="WithoutStep">Only cases that never went through this step, or <c>null</c>.</param>
/// <param name="Property">
/// One classification of the case: its kind, its area, purchase or sale. Without it every document kind is
/// the same process and every duration is an average over things that have nothing to do with each other.
/// </param>
/// <param name="PropertyValue">
/// Which value of it, or <c>null</c> for "classified at all" — the question behind every coverage gap.
/// </param>
public sealed record Scope(
    Period Period,
    string? Group,
    string? HasStep = null,
    string? WithoutStep = null,
    string? Property = null,
    string? PropertyValue = null
)
{
    /// <summary>No filter at all — every case, everybody.</summary>
    public static Scope Everything { get; } = new(Period.All, null);

    /// <summary>Reads a scope off the query string. An empty or blank value is no filter, not a filter on "".</summary>
    public static Scope FromQuery(
        string? from,
        string? until,
        string? group,
        string? hasStep = null,
        string? withoutStep = null,
        string? property = null,
        string? propertyValue = null
    ) =>
        new(
            Period.FromQuery(from, until),
            Trimmed(group),
            Trimmed(hasStep),
            Trimmed(withoutStep),
            Trimmed(property),
            Trimmed(propertyValue)
        );

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Every parameter the scope predicates expect. Always bound, so the SQL text never varies by request and the
    /// plan cache is not split between filtered and unfiltered calls.
    /// </summary>
    public (string Name, object Value)[] Parameters() =>
        [
            .. Period.Parameters(),
            ("scopeGroup", Group is null ? DBNull.Value : Group),
            ("scopeHasStep", HasStep is null ? DBNull.Value : HasStep),
            ("scopeWithoutStep", WithoutStep is null ? DBNull.Value : WithoutStep),
            ("scopeProperty", Property is null ? DBNull.Value : Property),
            ("scopePropertyValue", PropertyValue is null ? DBNull.Value : PropertyValue),
        ];

    /// <summary>
    /// The predicate for a relation that carries one row per case, or per event of a case, with the case start in
    /// <c>first_ts</c> and the case in <c>object_id</c>.
    /// </summary>
    /// <param name="alias">Table alias including the dot, or empty when the columns are unqualified.</param>
    public static string CaseFilter(string alias = "") =>
        $"""
            (@periodFrom::timestamptz IS NULL OR {alias}first_ts >= @periodFrom)
                      AND (@periodUntil::timestamptz IS NULL OR {alias}first_ts < @periodUntil)
                      AND analytics.case_touched_by_group({alias}object_id, @scopeGroup)
            """;

    /// <summary>
    /// The predicate for a relation of raw events (<c>ocel.event</c>), which has no case start to window on. Only the
    /// group applies; the window would need a case, and an event outside one belongs to no case by definition.
    /// </summary>
    /// <param name="alias">Table alias including the dot.</param>
    public static string EventFilter(string alias = "") => $"analytics.event_in_group({alias}id, @scopeGroup)";
}
