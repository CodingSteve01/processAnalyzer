using System.Diagnostics;

namespace ProcessAnalyzer.Web.Sync;

// Support for the pull loop (see JournalPullService.cs): the read-only guard on the source and the sync.run
// bookkeeping. Kept out of that file so the loop and the watermark rule can be read as one piece.
public sealed partial class JournalPullService
{
    /// <summary>
    /// The source is a live production database and this app has no business writing to it. A login that could
    /// write is a configuration error, not a warning: refuse to pull at all until it is fixed or waved through.
    /// Checked before the run row is opened, so a misconfiguration does not fill sync.run with refusals.
    /// </summary>
    private async Task EnsureReadOnlySourceAsync(CancellationToken ct)
    {
        if (_readOnlyVerified || _options.AllowWriteCapableLogin)
            return;

        if (await _source.IsWriteCapableAsync(ct))
        {
            throw new InvalidOperationException(
                "The configured source login can write to the source database. Use a read-only login, or set "
                    + "ProcessAnalyzer:AllowWriteCapableLogin when that is genuinely intended."
            );
        }

        _readOnlyVerified = true;
    }

    private async Task FinishAsync(long runId, RunTally tally, long startedAt, string? error, CancellationToken ct)
    {
        if (runId == 0)
            return;

        try
        {
            var elapsedMs = (int)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            await _mirror.FinishRunAsync(
                runId,
                tally.FromId,
                tally.ToId,
                tally.Events,
                tally.Objects,
                tally.HeldBack,
                tally.GapsFound,
                elapsedMs,
                error,
                ct
            );
        }
        catch (Exception ex)
        {
            // Bookkeeping must never mask the run it describes; the data work is already done or already failed.
            _logger.LogWarning(ex, "Could not close sync run {RunId}", runId);
        }
    }

    // What a run row records, carried as one value so the bookkeeping call sites stay one line each.
    private readonly record struct RunTally(
        long FromId,
        long ToId,
        int Events,
        int Objects,
        int HeldBack,
        int GapsFound
    );
}
