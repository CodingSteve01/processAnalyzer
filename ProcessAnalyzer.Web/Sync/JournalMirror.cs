using Microsoft.EntityFrameworkCore;
using ProcessAnalyzer.Web.Data;
using ProcessAnalyzer.Web.Models;

namespace ProcessAnalyzer.Web.Sync;

// The write side of the mirror: everything that touches our own Postgres lives here, so the pull loop stays
// pure decision logic and can be reasoned about without a database in the way.
// This file holds the EF-side work (cursor, runs, status). The bulk INSERT path speaks raw SQL because EF has
// no upsert, and lives in JournalMirror.Writes.cs.
public sealed partial class JournalMirror
{
    private const string CursorName = "journal";

    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly ILogger<JournalMirror> _logger;

    public JournalMirror(IDbContextFactory<AppDbContext> factory, ILogger<JournalMirror> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task<long> GetWatermarkAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var cursor = await db.Cursors.AsNoTracking().FirstOrDefaultAsync(c => c.Name == CursorName, ct);
        return cursor?.Value ?? 0;
    }

    public async Task SetWatermarkAsync(long value, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var cursor = await db.Cursors.FirstOrDefaultAsync(c => c.Name == CursorName, ct);
        if (cursor is null)
        {
            db.Cursors.Add(
                new SyncCursor
                {
                    Name = CursorName,
                    Value = value,
                    UpdatedAt = DateTime.UtcNow,
                }
            );
        }
        else
        {
            cursor.Value = value;
            cursor.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Returns the subset of <paramref name="ids"/> already mirrored. The gap sweep uses it as an anti-join.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> FilterKnownEventIdsAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
            return [];

        await using var db = await _factory.CreateDbContextAsync(ct);
        var known = new List<Guid>(ids.Count);

        // Chunked so a multi-day sweep does not build one IN list with tens of thousands of literals.
        foreach (var chunk in ids.Chunk(InsertBatchSize))
        {
            // A List, not the Guid[] the chunker hands back: EF cannot translate Contains over an array parameter
            // and fails inside expression compilation rather than at build time.
            var probe = chunk.ToList();
            var hits = await db
                .Events.AsNoTracking()
                .Where(e => probe.Contains(e.EventId))
                .Select(e => e.EventId)
                .ToListAsync(ct);
            known.AddRange(hits);
        }

        return known;
    }

    public async Task<long> StartRunAsync(string kind, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var run = new SyncRun { Kind = kind, StartedAt = DateTime.UtcNow };
        db.Runs.Add(run);
        await db.SaveChangesAsync(ct);
        return run.Id;
    }

    public async Task FinishRunAsync(
        long runId,
        long fromId,
        long toId,
        int events,
        int objects,
        int heldBack,
        int gapsFound,
        int elapsedMs,
        string? error,
        CancellationToken ct
    )
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var run = await db.Runs.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null)
            return;

        run.FinishedAt = DateTime.UtcNow;
        run.FromId = fromId;
        run.ToId = toId;
        run.Events = events;
        run.Objects = objects;
        run.HeldBack = heldBack;
        run.GapsFound = gapsFound;
        run.ElapsedMs = elapsedMs;
        run.Error = error;
        await db.SaveChangesAsync(ct);
    }

    public async Task<MirrorStatus> GetStatusAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        var cursor = await db.Cursors.AsNoTracking().FirstOrDefaultAsync(c => c.Name == CursorName, ct);
        var maxSourceId = await db.Events.AsNoTracking().MaxAsync(e => (long?)e.SourceId, ct) ?? 0;
        var eventCount = await db.Events.AsNoTracking().LongCountAsync(ct);
        var objectCount = await db.EventObjects.AsNoTracking().LongCountAsync(ct);

        var lastSuccess = await db
            .Runs.AsNoTracking()
            .Where(r => r.FinishedAt != null && r.Error == null)
            .OrderByDescending(r => r.Id)
            .Select(r => r.FinishedAt)
            .FirstOrDefaultAsync(ct);

        // The error of the MOST RECENT finished run, not the most recent error there has ever been. Reporting an old
        // failure as the current state leaves /health at 503 forever and trains everyone to ignore it.
        var lastError = await db
            .Runs.AsNoTracking()
            .Where(r => r.FinishedAt != null)
            .OrderByDescending(r => r.Id)
            .Select(r => r.Error)
            .FirstOrDefaultAsync(ct);

        var runs = await db
            .Runs.AsNoTracking()
            .OrderByDescending(r => r.Id)
            .Take(10)
            .Select(r => new MirrorRunSummary(
                r.Id,
                r.Kind,
                r.StartedAt,
                r.FinishedAt,
                r.FromId,
                r.ToId,
                r.Events,
                r.Objects,
                r.HeldBack,
                r.GapsFound,
                r.ElapsedMs,
                r.Error
            ))
            .ToListAsync(ct);

        // Watermark and highest mirrored id are reported separately on purpose: a gap between them means a run
        // died between the write and the watermark update, and one combined number would hide exactly that.
        return new MirrorStatus(cursor?.Value ?? 0, maxSourceId, eventCount, objectCount, lastSuccess, lastError, runs);
    }
}

public sealed record MirrorStatus(
    long Watermark,
    long MaxSourceId,
    long EventCount,
    long ObjectCount,
    DateTime? LastSuccessfulRunAt,
    string? LastError,
    IReadOnlyList<MirrorRunSummary> RecentRuns
);

public sealed record MirrorRunSummary(
    long Id,
    string Kind,
    DateTime StartedAt,
    DateTime? FinishedAt,
    long? FromId,
    long? ToId,
    int Events,
    int Objects,
    int HeldBack,
    int GapsFound,
    int? ElapsedMs,
    string? Error
);
