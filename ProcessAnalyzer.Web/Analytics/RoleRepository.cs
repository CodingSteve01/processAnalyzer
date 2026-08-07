using Microsoft.EntityFrameworkCore;
using ProcessAnalyzer.Web.Data;

namespace ProcessAnalyzer.Web.Analytics;

/// <summary>
/// Who somebody IS, from the views they set up for themselves.
/// </summary>
/// <remarks>
/// Every other question here is about what happened. This one is about who it happened by, and it cannot be answered
/// from the log: many people work in the same module and do completely different things with it, so a figure grouped by
/// module is an average over roles that have nothing to do with each other. The group directory does not answer it
/// either — a department is not a role.
/// <para>
/// Unscoped on purpose, all three. This is state, not a period: a person's role is not a thing that happened between
/// two dates, and narrowing it by the current window would make somebody look like a different person in March.
/// </para>
/// </remarks>
public sealed class RoleRepository
{
    private readonly IDbContextFactory<AppDbContext> _factory;

    public RoleRepository(IDbContextFactory<AppDbContext> factory) => _factory = factory;

    /// <summary>
    /// One row per person: how broadly they work, and how much they have set up.
    /// </summary>
    /// <remarks>
    /// Breadth is a role signal of its own. Somebody with views on a single screen is a specialist and somebody with
    /// views on thirty is not, and that difference is invisible in any per-module count.
    /// </remarks>
    public Task<List<Dictionary<string, object?>>> ProfilesAsync(CancellationToken ct) =>
        Query.RunAsync(
            _factory,
            """
            SELECT person, actor_key, masken, ansichten, filtermerkmale, zuletzt_gepflegt
            FROM analytics.view_profile
            """,
            ct
        );

    /// <summary>
    /// The filter vocabulary — read as a list, the role catalogue itself.
    /// </summary>
    /// <remarks>
    /// Each property names a way of looking at the business, and the number of people using it says how many hold that
    /// role. Names only: a filter value is free text somebody typed, and it would carry customer data into an
    /// analytical store for no analytical gain.
    /// </remarks>
    public Task<List<Dictionary<string, object?>>> VocabularyAsync(CancellationToken ct) =>
        Query.RunAsync(
            _factory,
            """
            SELECT property, personen, masken, ansichten
            FROM analytics.filter_vocabulary
            """,
            ct
        );

    /// <summary>
    /// Per screen: how many people work on it, and how differently.
    /// </summary>
    /// <remarks>
    /// The answer to "these seventy people all use the order screen — do they do the same thing?". A screen with many
    /// people and one filter is one role; many people and twenty filters is several sharing a surface.
    /// </remarks>
    public Task<List<Dictionary<string, object?>>> ScreensAsync(CancellationToken ct) =>
        Query.RunAsync(
            _factory,
            """
            SELECT path, personen, ansichten, verschiedene_filter, verschiedene_spalten
            FROM analytics.view_population
            """,
            ct
        );

    /// <summary>
    /// Which column layouts exist per screen, and how many people share each.
    /// </summary>
    /// <remarks>
    /// The question a user-interface rebuild has to answer. Not "which columns exist" — the schema says that — but which
    /// combinations people actually put on screen. A screen with sixty people on one layout can be rebuilt from that
    /// layout; a screen with eight people on eight layouts cannot be rebuilt until somebody looks at why.
    /// <para>
    /// Identified by the ORDERED column list: two people with the same columns in a different order have arranged their
    /// work differently, and collapsing them would hide the difference this is looking for.
    /// </para>
    /// </remarks>
    public Task<List<Dictionary<string, object?>>> LayoutsAsync(CancellationToken ct) =>
        Query.RunAsync(
            _factory,
            """
            SELECT path, layout_key, personen, ansichten, spalten, spaltenfolge
            FROM analytics.column_layout
            """,
            ct
        );

    /// <summary>
    /// Who shares a layout with whom.
    /// </summary>
    /// <remarks>
    /// The pair list behind the counts, so "these two work the same way" is a name rather than a number — and so a
    /// layout that looks shared but is one person with three saved views does not read as agreement.
    /// </remarks>
    public Task<List<Dictionary<string, object?>>> LayoutSharingAsync(CancellationToken ct) =>
        Query.RunAsync(
            _factory,
            """
            SELECT path, layout_key, person, teilt_mit
            FROM analytics.layout_sharing
            """,
            ct
        );
}
