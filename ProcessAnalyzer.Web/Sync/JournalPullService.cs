using System.Diagnostics;
using ProcessAnalyzer.Web.Options;

namespace ProcessAnalyzer.Web.Sync;

// Pulls the business-event journal into our mirror. The loop is deliberately dumb: read a page, accept
// the part of it that has certainly settled, advance the watermark, stop. Anything cleverer risks losing rows.
// The read-only guard and the sync.run bookkeeping live in JournalPullService.Bookkeeping.cs.
public sealed partial class JournalPullService : BackgroundService
{
    // One pull or sweep at a time, process-wide. Two pulls would read from the same watermark and both advance
    // it, so the second would move it past work the first had not written yet. The sweep shares the gate because
    // it hits the same source and the same tables; letting it interleave buys nothing.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static readonly PullResult Skipped = new(false, 0, 0, 0, 0, 0, null);

    private readonly IJournalSource _source;
    private readonly JournalMirror _mirror;
    private readonly ProcessAnalyzerOptions _options;
    private readonly ILogger<JournalPullService> _logger;
    private bool _readOnlyVerified;

    public JournalPullService(
        IJournalSource source,
        JournalMirror mirror,
        ProcessAnalyzerOptions options,
        ILogger<JournalPullService> logger
    )
    {
        _source = source;
        _mirror = mirror;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.PullIntervalSeconds));
        var sweepEvery = TimeSpan.FromMinutes(Math.Max(1, _options.GapSweepIntervalMinutes));
        var nextSweep = DateTime.UtcNow + sweepEvery;

        using var timer = new PeriodicTimer(interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var pull = await PullOnceAsync(stoppingToken);
                if (pull.Error is not null)
                    _logger.LogWarning("Pull tick reported an error: {Error}", pull.Error);

                if (DateTime.UtcNow >= nextSweep)
                {
                    nextSweep = DateTime.UtcNow + sweepEvery;
                    await SweepOnceAsync(stoppingToken);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // Both methods already swallow their own failures, so this only catches the unexpected. The loop
                // has to outlive anything that happens inside a single tick, or the mirror silently stops.
                _logger.LogError(ex, "Journal pull tick failed");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                    return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Reads forward from the watermark, page by page, and mirrors what has certainly settled.
    /// Never throws: failures are logged, recorded on the run row and returned in <see cref="PullResult.Error"/>.
    /// </summary>
    /// <summary>
    /// True when a journal source is configured. An unconfigured sidecar must not open run rows: a run that could
    /// never read anything would fill the history with refusals and make "the pull is idle" indistinguishable from
    /// "the pull is broken".
    /// </summary>
    private bool HasSource => !string.IsNullOrWhiteSpace(_options.SourceConnectionString);

    private static readonly PullResult NoSource = new(false, 0, 0, 0, 0, 0, "no journal source configured");

    public async Task<PullResult> PullOnceAsync(CancellationToken ct)
    {
        if (!HasSource)
            return NoSource;

        if (!await Gate.WaitAsync(0, ct))
            return Skipped;

        var startedAt = Stopwatch.GetTimestamp();
        var batchSize = Math.Max(1, _options.BatchSize);
        var maxPages = Math.Max(1, _options.MaxPagesPerRun);
        var runId = 0L;
        var fromId = 0L;
        var watermark = 0L;
        var events = 0;
        var objects = 0;
        var heldBack = 0;
        var pages = 0;

        try
        {
            await EnsureReadOnlySourceAsync(ct);

            watermark = await _mirror.GetWatermarkAsync(ct);
            fromId = watermark;
            runId = await _mirror.StartRunAsync("pull", ct);

            while (pages < maxPages)
            {
                var page = await _source.ReadEventsAsync(watermark, batchSize, ct);
                if (page.Count == 0)
                    break;

                pages++;
                var accepted = TakeSettledPrefix(page, DateTime.UtcNow.AddSeconds(-_options.LagSeconds));
                heldBack += page.Count - accepted.Count;
                if (accepted.Count == 0)
                    break;

                var ids = accepted.Select(e => e.SourceId).ToList();
                var pageObjects = await _source.ReadObjectsAsync(ids, ct);
                events += await _mirror.WriteEventsAsync(accepted, pageObjects, ct);
                objects += pageObjects.Count;

                // The watermark moves only after the write has committed. Crashing in between costs a re-read of
                // one page, which the ON CONFLICT rule absorbs; the other order would cost the page itself.
                watermark = accepted[^1].SourceId;
                await _mirror.SetWatermarkAsync(watermark, ct);

                // A short page means the source is drained. A truncated prefix means the very next id is still
                // inside the lag window, so there is nothing further this run may accept. Either way, stop.
                if (page.Count < batchSize || accepted.Count < page.Count)
                    break;
            }

            var tally = new RunTally(fromId, watermark, events, objects, heldBack, 0);
            await FinishAsync(runId, tally, startedAt, null, ct);
            return new PullResult(true, events, objects, heldBack, pages, 0, null);
        }
        catch (Exception ex)
        {
            // No retry inside a run: the watermark already matches whatever committed, and the next tick starts
            // from exactly there. Retrying here would only stack load on a source that is having a bad minute.
            _logger.LogError(ex, "Journal pull failed after {Pages} page(s), watermark {Watermark}", pages, watermark);
            var tally = new RunTally(fromId, watermark, events, objects, heldBack, 0);
            await FinishAsync(runId, tally, startedAt, ex.Message, CancellationToken.None);
            return new PullResult(true, events, objects, heldBack, pages, 0, ex.Message);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Re-checks the recent past below the watermark for events the pull never saw and mirrors the misses,
    /// without touching the watermark. Never throws.
    /// </summary>
    public async Task<PullResult> SweepOnceAsync(CancellationToken ct)
    {
        if (!HasSource)
            return NoSource;

        if (!await Gate.WaitAsync(0, ct))
            return Skipped;

        var startedAt = Stopwatch.GetTimestamp();
        var batchSize = Math.Max(1, _options.BatchSize);
        var runId = 0L;
        var watermark = 0L;
        var events = 0;
        var objects = 0;
        var gaps = 0;
        var pages = 0;

        try
        {
            await EnsureReadOnlySourceAsync(ct);

            watermark = await _mirror.GetWatermarkAsync(ct);
            runId = await _mirror.StartRunAsync("sweep", ct);

            var since = DateTime.UtcNow.AddDays(-Math.Max(1, _options.GapSweepDays));
            var keys = await _source.ReadKeysForSweepAsync(since, watermark, ct);
            var ids = keys.Select(k => k.EventId).ToList();
            var known = (await _mirror.FilterKnownEventIdsAsync(ids, ct)).ToHashSet();
            var missing = keys.Where(k => !known.Contains(k.EventId)).Select(k => k.SourceId).ToList();
            gaps = missing.Count;

            foreach (var chunk in missing.Chunk(batchSize))
            {
                var recovered = await _source.ReadEventsByIdAsync(chunk, ct);
                var recoveredObjects = await _source.ReadObjectsAsync(chunk, ct);
                events += await _mirror.WriteEventsAsync(recovered, recoveredObjects, ct);
                objects += recoveredObjects.Count;
                pages++;
            }

            if (gaps > 0)
            {
                // A non-zero result is a bug signal, not the sweep doing its job. It means rows committed below a
                // watermark the pull had already passed, i.e. LagSeconds is shorter than the source holds its
                // transactions open. The sweep only reaches back GapSweepDays; older losses are already permanent.
                _logger.LogWarning(
                    "Gap sweep found {Gaps} event(s) below the watermark and recovered {Events}. LagSeconds={Lag} is too small",
                    gaps,
                    events,
                    _options.LagSeconds
                );
            }

            // from == to: the sweep fills holes behind the watermark and must never move it forward.
            var tally = new RunTally(watermark, watermark, events, objects, 0, gaps);
            await FinishAsync(runId, tally, startedAt, null, ct);
            return new PullResult(true, events, objects, 0, pages, gaps, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gap sweep failed at watermark {Watermark}", watermark);
            var tally = new RunTally(watermark, watermark, events, objects, 0, gaps);
            await FinishAsync(runId, tally, startedAt, ex.Message, CancellationToken.None);
            return new PullResult(true, events, objects, 0, pages, gaps, ex.Message);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Accepts the contiguous prefix of the page, in id order, whose rows are older than the cutoff, and stops at
    /// the first row that is not.
    /// Filtering the page instead — taking every settled row and skipping unsettled ones in the middle — would
    /// advance the watermark past ids whose rows had not committed when we read. Identity columns hand out ids
    /// before commit, so such a row surfaces afterwards below the watermark, where nothing ever reads again apart
    /// from the shallow gap sweep. It is then gone forever, no error is raised anywhere, and the dashboard looks
    /// perfectly healthy while quietly under-counting.
    /// </summary>
    private static List<SourceEvent> TakeSettledPrefix(IReadOnlyList<SourceEvent> page, DateTime cutoffUtc)
    {
        // Sorted here rather than trusted from the query: the whole rule collapses if the page is not in id order.
        var ordered = page.OrderBy(e => e.SourceId).ToList();
        var accepted = new List<SourceEvent>(ordered.Count);

        foreach (var e in ordered)
        {
            if (e.RecordedAtUtc >= cutoffUtc)
                break;

            accepted.Add(e);
        }

        return accepted;
    }
}

public sealed record PullResult(
    bool Ran,
    int Events,
    int Objects,
    int HeldBack,
    int Pages,
    int GapsFound,
    string? Error
);
