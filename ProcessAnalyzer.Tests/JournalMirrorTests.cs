using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProcessAnalyzer.Web.Data;
using ProcessAnalyzer.Web.Sync;

namespace ProcessAnalyzer.Tests;

[Collection(PostgresCollection.Name)]
public sealed class JournalMirrorTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;

    public JournalMirrorTests(PostgresFixture postgres)
    {
        _postgres = postgres;
    }

    public Task InitializeAsync() => _postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// A fresh database must report 0, not throw and not null out. The first pull on a new
    /// deployment reads the watermark before anything has written one; if that read failed, the
    /// service would never get past its first run.
    /// </summary>
    [Fact]
    public async Task Watermark_StartsAtZero_AndRoundTripsTheLastValueWritten()
    {
        var mirror = new JournalMirror(_postgres.Factory, NullLogger<JournalMirror>.Instance);

        Assert.Equal(0, await mirror.GetWatermarkAsync(CancellationToken.None));

        await mirror.SetWatermarkAsync(42, CancellationToken.None);
        Assert.Equal(42, await mirror.GetWatermarkAsync(CancellationToken.None));

        // Overwrite, not insert-a-second-row: two cursor rows would make "the" watermark ambiguous
        // and the winner would depend on row order.
        await mirror.SetWatermarkAsync(99, CancellationToken.None);
        Assert.Equal(99, await mirror.GetWatermarkAsync(CancellationToken.None));

        await using var db = await _postgres.Factory.CreateDbContextAsync(CancellationToken.None);
        Assert.Equal(1, await db.Cursors.CountAsync());
    }

    /// <summary>
    /// The run history is the only place a failure is visible after the fact — the process may have
    /// restarted since. A run that finished without recording its error, or without an elapsed
    /// time, leaves an operator with nothing to look at but "it seems slow".
    /// </summary>
    [Fact]
    public async Task FinishRunAsync_RecordsElapsedAndError_SoAFailedRunStaysVisibleAfterARestart()
    {
        var mirror = new JournalMirror(_postgres.Factory, NullLogger<JournalMirror>.Instance);

        var runId = await mirror.StartRunAsync("pull", CancellationToken.None);
        await mirror.FinishRunAsync(
            runId,
            fromId: 100,
            toId: 107,
            events: 7,
            objects: 3,
            heldBack: 2,
            gapsFound: 0,
            elapsedMs: 23,
            error: "source timeout",
            ct: CancellationToken.None
        );

        await using var db = await _postgres.Factory.CreateDbContextAsync(CancellationToken.None);
        var run = await db.Runs.SingleAsync(r => r.Id == runId);
        Assert.Equal("pull", run.Kind);
        Assert.NotNull(run.FinishedAt);
        Assert.Equal(23, run.ElapsedMs);
        Assert.Equal("source timeout", run.Error);
        Assert.Equal(7, run.Events);
        Assert.Equal(2, run.HeldBack);
    }

    /// <summary>
    /// The gap sweep asks this question about a few thousand ids at a time and refills whatever
    /// comes back missing. If it reported an already-mirrored event as unknown the sweep would
    /// re-read it on every pass forever; if it reported a missing one as known the hole would never
    /// be filled — which is the failure the sweep exists to prevent.
    /// </summary>
    [Fact]
    public async Task FilterKnownEventIdsAsync_ReturnsExactlyTheIdsTheMirrorAlreadyHas()
    {
        var mirror = new JournalMirror(_postgres.Factory, NullLogger<JournalMirror>.Instance);
        var now = DateTime.UtcNow.AddMinutes(-10);
        await mirror.WriteEventsAsync(
            [FakeJournalSource.NewEvent(1, now), FakeJournalSource.NewEvent(2, now)],
            [],
            CancellationToken.None
        );

        var known = await mirror.FilterKnownEventIdsAsync(
            [FakeJournalSource.EventIdFor(1), FakeJournalSource.EventIdFor(2), FakeJournalSource.EventIdFor(3)],
            CancellationToken.None
        );

        Assert.Equal(2, known.Count);
        Assert.Contains(FakeJournalSource.EventIdFor(1), known);
        Assert.Contains(FakeJournalSource.EventIdFor(2), known);
        Assert.DoesNotContain(FakeJournalSource.EventIdFor(3), known);
    }

    /// <summary>
    /// The returned count is what the run history and the status page report as progress. Returning
    /// the number of rows offered rather than inserted would show a healthy, busy pull that is in
    /// fact re-reading the same page and moving nothing.
    /// </summary>
    [Fact]
    public async Task WriteEventsAsync_ReturnsOnlyTheEventsActuallyInserted()
    {
        var mirror = new JournalMirror(_postgres.Factory, NullLogger<JournalMirror>.Instance);
        var now = DateTime.UtcNow.AddMinutes(-10);
        var first = FakeJournalSource.NewEvent(1, now);
        var second = FakeJournalSource.NewEvent(2, now);
        var third = FakeJournalSource.NewEvent(3, now);
        var objects = new[] { FakeJournalSource.NewObject(10, 1, "order", "A-1") };

        var initial = await mirror.WriteEventsAsync([first, second], objects, CancellationToken.None);
        var replayed = await mirror.WriteEventsAsync([first, second, third], objects, CancellationToken.None);

        Assert.Equal(2, initial);
        Assert.Equal(1, replayed);

        await using var db = await _postgres.Factory.CreateDbContextAsync(CancellationToken.None);
        Assert.Equal(3, await db.Events.CountAsync());
        Assert.Equal(1, await db.EventObjects.CountAsync());
    }
}
