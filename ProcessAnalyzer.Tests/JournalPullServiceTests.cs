using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProcessAnalyzer.Web.Data;
using ProcessAnalyzer.Web.Options;
using ProcessAnalyzer.Web.Sync;

namespace ProcessAnalyzer.Tests;

[Collection(PostgresCollection.Name)]
public sealed class JournalPullServiceTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;

    public JournalPullServiceTests(PostgresFixture postgres)
    {
        _postgres = postgres;
    }

    public Task InitializeAsync() => _postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// The rule the entire pull exists for. A journal row becomes visible at commit time, not at
    /// insert time, so id order and visibility order are not the same. If the pull skipped the row
    /// inside the lag window and took the older row behind it, the watermark would move past the
    /// skipped id and that event would never be read again — a silent, permanent hole.
    /// </summary>
    [Fact]
    public async Task Pull_StopsAtTheFirstRowInsideTheLagWindow_AndDoesNotSkipPastIt()
    {
        var now = DateTime.UtcNow;
        var source = new FakeJournalSource([
            FakeJournalSource.NewEvent(1, now.AddMinutes(-10)),
            FakeJournalSource.NewEvent(2, now.AddMinutes(-9)),
            // Inside the 120 s window — the pull must stop here.
            FakeJournalSource.NewEvent(3, now),
            // Old enough on its own, but it sits behind row 3 and must not be taken.
            FakeJournalSource.NewEvent(4, now.AddMinutes(-8)),
        ]);
        var mirror = new JournalMirror(_postgres.Factory, NullLogger<JournalMirror>.Instance);
        var service = CreateService(source, mirror);

        var result = await service.PullOnceAsync(CancellationToken.None);

        Assert.True(result.Ran);
        Assert.Null(result.Error);
        Assert.Equal(2, result.Events);
        Assert.Equal(2, result.HeldBack);
        Assert.Equal(2, await mirror.GetWatermarkAsync(CancellationToken.None));
        Assert.Equal(new List<long> { 1, 2 }, await MirroredSourceIdsAsync());
    }

    /// <summary>
    /// A restarted container, a restored backup or a rewound cursor all replay a page that is
    /// already mirrored. Without ON CONFLICT that replay either throws on the unique key or, worse,
    /// duplicates every event and doubles every future count.
    /// </summary>
    [Fact]
    public async Task Pull_RunTwiceOverTheSamePage_InsertsNothingTheSecondTime()
    {
        var now = DateTime.UtcNow;
        var source = new FakeJournalSource(
            [FakeJournalSource.NewEvent(1, now.AddMinutes(-10)), FakeJournalSource.NewEvent(2, now.AddMinutes(-9))],
            [FakeJournalSource.NewObject(10, 1, "order", "A-1"), FakeJournalSource.NewObject(11, 2, "tour", "T-1")]
        );
        var mirror = new JournalMirror(_postgres.Factory, NullLogger<JournalMirror>.Instance);
        var service = CreateService(source, mirror);

        var first = await service.PullOnceAsync(CancellationToken.None);
        // Rewinding the cursor is how a replay is reproduced deliberately. Without it the second
        // run would read an empty page and the test would prove nothing about ON CONFLICT.
        await mirror.SetWatermarkAsync(0, CancellationToken.None);
        var second = await service.PullOnceAsync(CancellationToken.None);

        Assert.Equal(2, first.Events);
        Assert.Equal(0, second.Events);
        Assert.Null(second.Error);
        Assert.Equal(2, await CountEventsAsync());
        Assert.Equal(2, await CountObjectsAsync());
    }

    /// <summary>
    /// Not every business event touches an object the journal knows how to name. Those rows are
    /// legal and carry the timestamps a process log needs; dropping them because the object join
    /// came back empty would quietly shorten every trace they belong to.
    /// </summary>
    [Fact]
    public async Task Pull_EventWithoutObjectRows_IsStillMirrored()
    {
        var now = DateTime.UtcNow;
        var source = new FakeJournalSource(
            [FakeJournalSource.NewEvent(1, now.AddMinutes(-10)), FakeJournalSource.NewEvent(2, now.AddMinutes(-9))],
            [FakeJournalSource.NewObject(10, 1, "order", "A-1")]
        );
        var mirror = new JournalMirror(_postgres.Factory, NullLogger<JournalMirror>.Instance);
        var service = CreateService(source, mirror);

        var result = await service.PullOnceAsync(CancellationToken.None);

        Assert.Equal(2, result.Events);
        Assert.Equal(1, result.Objects);
        Assert.Equal(new List<long> { 1, 2 }, await MirroredSourceIdsAsync());

        await using var db = await _postgres.Factory.CreateDbContextAsync(CancellationToken.None);
        Assert.Empty(await db.EventObjects.Where(o => o.EventSourceId == 2).ToListAsync());
    }

    /// <summary>
    /// A payload the source wrote by hand, truncated, or double-encoded must not be able to abort a
    /// run. One unparsable row killing the batch would stall the watermark behind it forever, and
    /// every later event would stop arriving because of a single bad string.
    /// </summary>
    [Fact]
    public async Task Pull_MalformedPayload_IsStoredRawAndDoesNotKillTheRun()
    {
        var now = DateTime.UtcNow;
        const string Malformed = "{not json";
        var source = new FakeJournalSource([
            FakeJournalSource.NewEvent(1, now.AddMinutes(-10), """{"a":1}"""),
            FakeJournalSource.NewEvent(2, now.AddMinutes(-9), Malformed),
            FakeJournalSource.NewEvent(3, now.AddMinutes(-8), """{"b":2}"""),
        ]);
        var mirror = new JournalMirror(_postgres.Factory, NullLogger<JournalMirror>.Instance);
        var service = CreateService(source, mirror);

        var result = await service.PullOnceAsync(CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(3, result.Events);
        Assert.Equal(3, await mirror.GetWatermarkAsync(CancellationToken.None));

        await using var db = await _postgres.Factory.CreateDbContextAsync(CancellationToken.None);
        var bad = await db.Events.SingleAsync(e => e.SourceId == 2);
        Assert.Equal("{}", bad.Payload);
        Assert.Equal(Malformed, bad.PayloadRaw);
    }

    /// <summary>
    /// The lag window narrows the race, it does not close it: a transaction that stays open longer
    /// than LagSeconds still commits behind the watermark. The sweep is the only thing that finds
    /// those rows, and it must fill the hole without touching the watermark — moving it backwards
    /// would re-pull everything, moving it forwards would skip whatever the pull has not seen yet.
    /// </summary>
    [Fact]
    public async Task Sweep_FindsAnEventThatThePrefixRuleMissed_AndDoesNotMoveTheWatermark()
    {
        var now = DateTime.UtcNow;
        // Id 2 exists in the source but its transaction has not committed yet, so no reader sees it.
        var source = new FakeJournalSource([
            FakeJournalSource.NewEvent(1, now.AddMinutes(-30)),
            FakeJournalSource.NewEvent(3, now.AddMinutes(-28)),
        ]);
        var mirror = new JournalMirror(_postgres.Factory, NullLogger<JournalMirror>.Instance);
        var service = CreateService(source, mirror);

        await service.PullOnceAsync(CancellationToken.None);
        Assert.Equal(3, await mirror.GetWatermarkAsync(CancellationToken.None));

        source.Reveal(FakeJournalSource.NewEvent(2, now.AddMinutes(-29)));
        var afterReveal = await service.PullOnceAsync(CancellationToken.None);
        // The pull reads strictly after the watermark, so on its own it can never recover id 2.
        Assert.Equal(0, afterReveal.Events);
        Assert.Equal(new List<long> { 1, 3 }, await MirroredSourceIdsAsync());

        var sweep = await service.SweepOnceAsync(CancellationToken.None);

        Assert.Null(sweep.Error);
        Assert.Equal(1, sweep.GapsFound);
        Assert.Equal(1, sweep.Events);
        Assert.Equal(new List<long> { 1, 2, 3 }, await MirroredSourceIdsAsync());
        Assert.Equal(3, await mirror.GetWatermarkAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Pull_WithoutAConfiguredSource_DoesNotOpenARun()
    {
        var source = new FakeJournalSource();
        var mirror = new JournalMirror(_postgres.Factory, NullLogger<JournalMirror>.Instance);
        var service = new JournalPullService(
            source,
            mirror,
            new ProcessAnalyzerOptions(),
            NullLogger<JournalPullService>.Instance
        );

        var result = await service.PullOnceAsync(CancellationToken.None);

        // An unconfigured sidecar has to start and say so. What it must not do is fill the run history with
        // refusals — that would make "the pull is idle" indistinguishable from "the pull is broken".
        Assert.False(result.Ran);
        Assert.Equal("no journal source configured", result.Error);
        await using var db = await _postgres.Factory.CreateDbContextAsync(CancellationToken.None);
        Assert.Empty(await db.Runs.ToListAsync());
    }

    private JournalPullService CreateService(FakeJournalSource source, JournalMirror mirror)
    {
        var options = new ProcessAnalyzerOptions
        {
            // Any non-empty string counts as "a source is configured"; the fake stands in for the reader, so the
            // value is never parsed. Leave it empty and the service correctly refuses to run at all.
            SourceConnectionString = "configured-for-the-fake-source",
            LagSeconds = 120,
            BatchSize = 5000,
            MaxPagesPerRun = 40,
            GapSweepDays = 3,
        };
        return new JournalPullService(source, mirror, options, NullLogger<JournalPullService>.Instance);
    }

    private async Task<List<long>> MirroredSourceIdsAsync()
    {
        await using var db = await _postgres.Factory.CreateDbContextAsync(CancellationToken.None);
        return await db.Events.OrderBy(e => e.SourceId).Select(e => e.SourceId).ToListAsync();
    }

    private async Task<int> CountEventsAsync()
    {
        await using var db = await _postgres.Factory.CreateDbContextAsync(CancellationToken.None);
        return await db.Events.CountAsync();
    }

    private async Task<int> CountObjectsAsync()
    {
        await using var db = await _postgres.Factory.CreateDbContextAsync(CancellationToken.None);
        return await db.EventObjects.CountAsync();
    }
}
