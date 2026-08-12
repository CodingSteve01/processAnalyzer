namespace ProcessAnalyzer.Web.Models;

// One mirrored row of the business-event journal. This table is a mirror, not a model of its own:
// every column except the pull bookkeeping (PulledAt, ProjectionVersion, PayloadRaw) is a verbatim copy of
// the source row. Nothing here may be edited after insert: corrections come from re-reading the source.
public sealed class JournalEvent
{
    // BusinessEvents.Id in the source. Doubles as our cursor, which is why it is assigned by the source and
    // never generated here (see AppDbContext: ValueGeneratedNever).
    public long SourceId { get; set; }

    // Dedup key. The source id can in principle be reused after a restore; the event id cannot, so a re-pull
    // of an overlapping range collides here and is skipped instead of duplicating history.
    public Guid EventId { get; set; }

    public string EventType { get; set; } = "";

    // Business time: when the thing happened. This is the axis process mining runs on.
    public DateTime OccurredAt { get; set; }

    // Journal write time. Lags OccurredAt and is the axis the pull watermark reasons about, because only
    // this one is monotonic with the source id.
    public DateTime RecordedAt { get; set; }

    public string PerformerType { get; set; } = "";
    public string? PerformerId { get; set; }
    public string? InitiatorType { get; set; }
    public string? InitiatorId { get; set; }

    public string? CorrelationId { get; set; }
    public string? TraceId { get; set; }

    public string SourceApplication { get; set; } = "";
    public string? SourceModule { get; set; }
    public string? SourceVersion { get; set; }

    // jsonb. Empty object when the source had no payload: never null, so downstream projections can index
    // into it without a null branch per query.
    public string Payload { get; set; } = "{}";

    // Set ONLY when the source text failed to parse as jsonb. Keeping the unparseable text instead of
    // dropping the event is the whole point: a malformed payload must not cost us the event itself.
    public string? PayloadRaw { get; set; }

    public long? MandateId { get; set; }

    // When we mirrored the row. Separate from RecordedAt so pull lag is measurable after the fact.
    public DateTime PulledAt { get; set; }

    // 0 = not yet projected. Phase 2 raises this per event once the OCEL projection has consumed it; the
    // partial index on (source_id) WHERE projection_version = 0 is the work queue.
    public int ProjectionVersion { get; set; }
}
