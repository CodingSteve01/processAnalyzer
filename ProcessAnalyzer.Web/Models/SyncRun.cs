namespace ProcessAnalyzer.Web.Models;

// One pull or sweep attempt. Persisted even when it fails, because "the mirror is complete" is a claim that
// can only be defended with an unbroken run history — a pull that silently did nothing looks identical to a
// pull that never started unless it left a row here.
public sealed class SyncRun
{
    public long Id { get; set; }

    // "pull" or "sweep".
    public string Kind { get; set; } = "";

    public DateTime StartedAt { get; set; }

    // Null while the run is in flight; a run stuck with FinishedAt null is the signal for a crashed pull.
    public DateTime? FinishedAt { get; set; }

    public long? FromId { get; set; }
    public long? ToId { get; set; }

    public int Events { get; set; }
    public int Objects { get; set; }

    // Events read from the source but deliberately not committed yet because they fall inside the lag
    // window. Not a loss — they get picked up once the source has settled.
    public int HeldBack { get; set; }

    // Source ids the sweep found missing from the mirror. Anything above zero here means the plain
    // watermark path lost events and the sweep is the only reason we still have them.
    public int GapsFound { get; set; }

    public int? ElapsedMs { get; set; }
    public string? Error { get; set; }
}
