namespace ProcessAnalyzer.Web.Sync;

public sealed record SourceEvent(
    long SourceId,
    Guid EventId,
    string EventType,
    DateTime OccurredAtUtc,
    DateTime RecordedAtUtc,
    string PerformerType,
    string? PerformerId,
    string? InitiatorType,
    string? InitiatorId,
    string? CorrelationId,
    string? TraceId,
    string SourceApplication,
    string? SourceModule,
    string? SourceVersion,
    string? PayloadJson,
    long? MandateId
);

public sealed record SourceEventObject(
    long SourceId,
    long EventSourceId,
    string ObjectType,
    string ObjectId,
    string Qualifier
);

/// The one interface in the project. The whole point of phase 1 is proving the watermark rule, and that has to be
/// testable without a SQL Server. The seam is here and nowhere else — an interface per class buys nothing.
public interface IJournalSource
{
    Task<IReadOnlyList<SourceEvent>> ReadEventsAsync(long afterId, int batchSize, CancellationToken ct);
    Task<IReadOnlyList<SourceEventObject>> ReadObjectsAsync(IReadOnlyList<long> eventSourceIds, CancellationToken ct);
    Task<IReadOnlyList<SourceEventKey>> ReadKeysForSweepAsync(DateTime sinceUtc, long maxId, CancellationToken ct);
    Task<IReadOnlyList<SourceEvent>> ReadEventsByIdAsync(IReadOnlyList<long> sourceIds, CancellationToken ct);
    Task<bool> IsWriteCapableAsync(CancellationToken ct);
}

public sealed record SourceEventKey(long SourceId, Guid EventId);
