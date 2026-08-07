using System.Text.Json;

namespace ProcessAnalyzer.Web.Sync;

/// <summary>
/// Reads a saved view's payload and returns the two things in it that describe somebody's work.
/// </summary>
/// <remarks>
/// Separate from <see cref="SavedViewSync" /> because it is the only part with a decision in it: the sync moves rows,
/// this decides what a role signal is. It also means the rule that matters most here can be asserted without a database
/// — that filter VALUES never leave this method.
/// <para>
/// <b>Names only.</b> A filter value is free text somebody typed: a customer name, a licence plate, a vendor. The
/// property name is a schema identifier and answers the role question on its own, so the value is dropped here rather
/// than stored and forgotten about. The source grant is necessarily wider than that — SQL Server cannot grant half a
/// column — which is exactly why the restraint has to live in code and be tested.
/// </para>
/// </remarks>
public static class SavedViewPayload
{
    /// <summary>An upper bound on collected columns, so one malformed payload cannot fill the table.</summary>
    private const int MaxColumns = 500;

    /// <summary>
    /// The filter properties and the visible columns, in the order the view puts them.
    /// </summary>
    /// <remarks>
    /// Never throws. A payload that cannot be read is one view missing from one person's signature; an exception would
    /// cost the whole pull, and the whole pull is what answers the question.
    /// </remarks>
    public static (List<string> Filters, List<string> Columns) Decompose(string? data)
    {
        var filters = new List<string>();
        var columns = new List<string>();
        if (string.IsNullOrWhiteSpace(data))
            return (filters, columns);

        try
        {
            using var document = JsonDocument.Parse(data);

            // The kind is checked, not just the presence. Asking a number for a property throws
            // InvalidOperationException rather than JsonException, so a payload whose "definition" is not an object
            // would escape the guard below and take the whole pull down with it.
            if (
                document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("definition", out var definition)
                || definition.ValueKind != JsonValueKind.Object
            )
                return (filters, columns);

            CollectFilters(definition, filters);
            CollectColumns(definition, columns);
        }
        catch (JsonException)
        {
            return (filters, columns);
        }

        return (filters, columns);
    }

    /// <summary>The properties a view narrows by — never what it narrows them to.</summary>
    private static void CollectFilters(JsonElement definition, List<string> filters)
    {
        if (!definition.TryGetProperty("Filters", out var array) || array.ValueKind != JsonValueKind.Array)
            return;

        foreach (var entry in array.EnumerateArray())
        {
            if (
                entry.ValueKind == JsonValueKind.Object
                && entry.TryGetProperty("PropertyName", out var name)
                && name.ValueKind == JsonValueKind.String
                && name.GetString() is { Length: > 0 } property
                && !filters.Contains(property, StringComparer.Ordinal)
            )
                filters.Add(property);
        }
    }

    /// <summary>
    /// Walks the group tree and collects column properties in the order they appear.
    /// </summary>
    /// <remarks>
    /// Columns nest in groups to any depth — fixed-left, horizontal, further groups — so they are walked rather than
    /// read from a known level. The <c>Filters</c> array is skipped explicitly: it also carries
    /// <c>PropertyName</c> entries, and reading them as columns would make every filtered property look like a visible
    /// one.
    /// </remarks>
    private static void CollectColumns(JsonElement element, List<string> columns)
    {
        if (columns.Count >= MaxColumns)
            return;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var member in element.EnumerateObject())
                {
                    if (string.Equals(member.Name, "Filters", StringComparison.Ordinal))
                        continue;

                    if (
                        string.Equals(member.Name, "PropertyName", StringComparison.Ordinal)
                        && member.Value.ValueKind == JsonValueKind.String
                        && member.Value.GetString() is { Length: > 0 } property
                        && !columns.Contains(property, StringComparer.Ordinal)
                    )
                    {
                        columns.Add(property);
                        continue;
                    }

                    CollectColumns(member.Value, columns);
                }

                break;

            case JsonValueKind.Array:
                foreach (var entry in element.EnumerateArray())
                    CollectColumns(entry, columns);
                break;
        }
    }
}
