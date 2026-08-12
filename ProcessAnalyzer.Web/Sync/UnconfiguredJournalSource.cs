namespace ProcessAnalyzer.Web.Sync;

/// <summary>
/// The source when none is configured.
/// <para>
/// Running without a source is legitimate, so the application must start. Every method throws rather than returning
/// an empty result: an empty result is indistinguishable from "the journal had nothing new", which would let an
/// unconfigured mirror report itself healthy indefinitely.
/// </para>
/// <para>
/// <see cref="JournalPullService"/> checks for a configured source before starting a run, so nothing calls these
/// methods today. This type guards against a caller added later that does not check.
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
    /// False: there is no login, so nothing could write. The startup guard covers a real login with too many rights,
    /// not the absence of one.
    /// </summary>
    public Task<bool> IsWriteCapableAsync(CancellationToken ct) => Task.FromResult(false);
}
