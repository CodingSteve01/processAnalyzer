namespace ProcessAnalyzer.Web.Models;

// Named watermark. One row per cursor so the gap sweep and any later projection can each keep their own
// position without fighting over a single global value.
public sealed class SyncCursor
{
    public string Name { get; set; } = "";

    // Highest source id that is safely mirrored. Only ever moves forward, and only after the corresponding
    // rows are committed — advancing it first would silently skip events with no way to notice.
    public long Value { get; set; }

    public DateTime UpdatedAt { get; set; }
}
