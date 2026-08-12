namespace ProcessAnalyzer.Web.Models;

// The object side of the journal, which business objects an event touched, and in which role. This is what
// makes the data object-centric later: an event is not "an order event", it is an event that references an
// order, a tour and a vehicle with different qualifiers.
public sealed class JournalEventObject
{
    // BusinessEventObjects.Id in the source, mirrored verbatim (ValueGeneratedNever, see AppDbContext).
    public long SourceId { get; set; }

    // FK to JournalEvent.SourceId, cascade delete. Deliberately no navigation property: the mirror writes
    // events and objects as two independent flat batches, and a navigation would let EF build a graph whose
    // insert order and fix-up we would then have to reason about on every pull.
    public long EventSourceId { get; set; }

    public string ObjectType { get; set; } = "";
    public string ObjectId { get; set; } = "";

    // Role of the object in this event (e.g. subject vs. context). Part of the natural key, so the same
    // object may legitimately appear twice on one event under different qualifiers.
    public string Qualifier { get; set; } = "";
}
