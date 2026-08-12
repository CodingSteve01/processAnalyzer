using ProcessAnalyzer.Web.Sync;

namespace ProcessAnalyzer.Tests;

/// <summary>
/// An <see cref="IJournalSource"/> whose rows are handed to it as a list, so a test can build the
/// exact situation it needs: a page whose middle row is inside the lag window, or a row that only
/// becomes visible after the watermark has already passed its id.
/// <para>
/// The fake deliberately does NOT filter by the lag window. Applying the lag is the pull's job; a
/// fake that pre-filtered would make the lag tests pass no matter what the pull does.
/// </para>
/// </summary>
public sealed class FakeJournalSource : IJournalSource
{
    private readonly List<SourceEvent> _visible = [];
    private readonly List<SourceEventObject> _objects = [];

    public FakeJournalSource(IEnumerable<SourceEvent>? events = null, IEnumerable<SourceEventObject>? objects = null)
    {
        if (events is not null)
        {
            _visible.AddRange(events);
        }

        if (objects is not null)
        {
            _objects.AddRange(objects);
        }
    }

    public bool WriteCapable { get; set; }

    /// <summary>Number of pages the pull asked for, asserting that MaxPagesPerRun is honoured.</summary>
    public int ReadEventsCalls { get; private set; }

    /// <summary>
    /// Makes a row visible that was not there before. This is what a late commit looks like from a
    /// reader's side: the id was already allocated, but the row only appeared after a reader had
    /// moved past it.
    /// </summary>
    public void Reveal(SourceEvent sourceEvent, params SourceEventObject[] objects)
    {
        _visible.Add(sourceEvent);
        _objects.AddRange(objects);
    }

    public Task<IReadOnlyList<SourceEvent>> ReadEventsAsync(long afterId, int batchSize, CancellationToken ct)
    {
        ReadEventsCalls++;
        IReadOnlyList<SourceEvent> page =
        [
            .. _visible.Where(e => e.SourceId > afterId).OrderBy(e => e.SourceId).Take(batchSize),
        ];
        return Task.FromResult(page);
    }

    public Task<IReadOnlyList<SourceEventObject>> ReadObjectsAsync(
        IReadOnlyList<long> eventSourceIds,
        CancellationToken ct
    )
    {
        var wanted = eventSourceIds.ToHashSet();
        IReadOnlyList<SourceEventObject> rows =
        [
            .. _objects.Where(o => wanted.Contains(o.EventSourceId)).OrderBy(o => o.SourceId),
        ];
        return Task.FromResult(rows);
    }

    public Task<IReadOnlyList<SourceEventKey>> ReadKeysForSweepAsync(
        DateTime sinceUtc,
        long maxId,
        CancellationToken ct
    )
    {
        IReadOnlyList<SourceEventKey> keys =
        [
            .. _visible
                .Where(e => e.RecordedAtUtc >= sinceUtc && e.SourceId <= maxId)
                .OrderBy(e => e.SourceId)
                .Select(e => new SourceEventKey(e.SourceId, e.EventId)),
        ];
        return Task.FromResult(keys);
    }

    public Task<IReadOnlyList<SourceEvent>> ReadEventsByIdAsync(IReadOnlyList<long> sourceIds, CancellationToken ct)
    {
        var wanted = sourceIds.ToHashSet();
        IReadOnlyList<SourceEvent> rows =
        [
            .. _visible.Where(e => wanted.Contains(e.SourceId)).OrderBy(e => e.SourceId),
        ];
        return Task.FromResult(rows);
    }

    public Task<bool> IsWriteCapableAsync(CancellationToken ct) => Task.FromResult(WriteCapable);

    /// <summary>
    /// Builds a source event. <paramref name="recordedAtUtc"/> is the only field most tests care
    /// about, because it is the field the lag rule reads.
    /// </summary>
    public static SourceEvent NewEvent(
        long id,
        DateTime recordedAtUtc,
        string? payloadJson = """{"ok":true}""",
        string eventType = "test.event"
    ) =>
        new(
            SourceId: id,
            EventId: EventIdFor(id),
            EventType: eventType,
            OccurredAtUtc: recordedAtUtc,
            RecordedAtUtc: recordedAtUtc,
            PerformerType: "system",
            PerformerId: null,
            InitiatorType: null,
            InitiatorId: null,
            CorrelationId: null,
            TraceId: null,
            SourceApplication: "test",
            SourceModule: null,
            SourceVersion: null,
            PayloadJson: payloadJson,
            MandateId: null
        );

    public static SourceEventObject NewObject(long id, long eventSourceId, string objectType, string objectId) =>
        new(
            SourceId: id,
            EventSourceId: eventSourceId,
            ObjectType: objectType,
            ObjectId: objectId,
            Qualifier: "subject"
        );

    /// <summary>
    /// Derived from the source id so the same row keeps the same event id when it is revealed later
    /// Otherwise the gap sweep would treat a re-appearing row as a new event and hide the bug.
    /// </summary>
    public static Guid EventIdFor(long id)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, id);
        return new Guid(bytes);
    }
}
