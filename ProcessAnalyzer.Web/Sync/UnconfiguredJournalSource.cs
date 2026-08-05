namespace ProcessAnalyzer.Web.Sync;

/// <summary>
/// The source when none is configured.
/// <para>
/// A sidecar with no source is a legitimate state — a fresh checkout, a developer running the dashboard, a
/// deployment waiting for its read-only login. It must start, and it must say so. What it must never do is look
/// like it is working: every method here throws rather than returning an empty page, because an empty page is
/// indistinguishable from "the journal had nothing new" and would let an unconfigured mirror report itself healthy
/// forever.
/// </para>
/// <para>
/// Nothing calls these methods in practice — <see cref="JournalPullService"/> checks for a configured source before
/// it starts a run. This type is the second net, for the day someone adds a caller that does not.
/// </para>
/// </summary>
public sealed class UnconfiguredJournalSource : IJournalSource
{
    private const string Message =
        "No journal source is configured. Set ProcessAnalyzer:SourceConnectionString to a read-only the source login.";

    public Task<IReadOnlyList<SourceEvent>> ReadEventsAsync(long afterId, int batchSize, CancellationToken ct) =>
        throw new InvalidOperationException(Message);

    public Task<IReadOnlyList<SourceEventObject>> ReadObjectsAsync(
        IReadOnlyList<long> eventSourceIds,
        CancellationToken ct
    ) => throw new InvalidOperationException(Message);

    public Task<IReadOnlyList<SourceEventKey>> ReadKeysForSweepAsync(
        DateTime sinceUtc,
        long maxId,
        CancellationToken ct
    ) => throw new InvalidOperationException(Message);

    public Task<IReadOnlyList<SourceEvent>> ReadEventsByIdAsync(IReadOnlyList<long> sourceIds, CancellationToken ct) =>
        throw new InvalidOperationException(Message);

    /// <summary>
    /// False: there is no login, so there is nothing that could write. The startup guard is about a real login with
    /// too many rights, not about the absence of one.
    /// </summary>
    public Task<bool> IsWriteCapableAsync(CancellationToken ct) => Task.FromResult(false);
}
