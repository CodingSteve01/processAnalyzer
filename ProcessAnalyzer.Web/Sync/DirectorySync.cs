using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProcessAnalyzer.Web.Data;
using ProcessAnalyzer.Web.Options;

namespace ProcessAnalyzer.Web.Sync;

/// <summary>
/// Pulls the user directory, meaning who exists and which groups they belong to, and stores it as the actor
/// dimension.
/// <para>
/// Master data, not events: read whole and replaced, so a removed membership disappears instead of lingering. Small
/// enough that incremental syncing would cost more machinery than it saves.
/// </para>
/// <para>
/// The pseudonym is computed here with the same key the projection uses, so <c>ocel.*</c> joins to a role without
/// the raw user id leaving <c>dim.actor</c>. The analysis speaks in roles, the mapping to a person lives in one
/// table, and no endpoint reads it.
/// </para>
/// </summary>
public sealed class DirectorySync
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly ProcessAnalyzerOptions _options;
    private readonly ILogger<DirectorySync> _logger;

    public DirectorySync(
        IDbContextFactory<AppDbContext> factory,
        ProcessAnalyzerOptions options,
        ILogger<DirectorySync> logger
    )
    {
        _factory = factory;
        _options = options;
        _logger = logger;
    }

    public async Task<DirectoryResult> SyncAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.SourceConnectionString))
            return new DirectoryResult(0, 0, 0, null, "no source configured");

        var (users, memberships) = await ReadDirectoryAsync(ct);
        await WriteAsync(users, memberships, ct);
        var calendar = await SyncCalendarAsync(ct);

        _logger.LogInformation(
            "Directory synced: {Users} users, {Memberships} group memberships",
            users.Count,
            memberships.Count
        );
        return new DirectoryResult(users.Count, memberships.Count, calendar.Holidays, calendar.Calendar, null);
    }

    private async Task<(
        List<(string Id, string? Name, bool IsActive)> Users,
        List<(string UserId, string Group)> Memberships
    )> ReadDirectoryAsync(CancellationToken ct)
    {
        await using var connection = new SqlConnection(_options.SourceConnectionString);
        await connection.OpenAsync(ct);

        var users = new List<(string, string?, bool)>();
        await using (var command = connection.CreateCommand())
        {
            // dbo.AspNetUsers, not dbo.Users: the source keeps its people in the Identity table. There is no
            // DisplayName column, so the name is assembled from FirstName and Surname and falls back to the login,
            // which keeps an actor from rendering as a blank cell.
            // Blocked and LeaveDate come along because somebody who left still appears in every group they were ever
            // in, and counting them as present turns "six people do this work" into a number nobody recognises.
            command.CommandText = """
                SELECT u.Id,
                       NULLIF(LTRIM(RTRIM(CONCAT(u.FirstName, ' ', u.Surname))), '') AS DisplayName,
                       u.UserName,
                       CASE WHEN u.Blocked = 1 THEN 0
                            WHEN u.LeaveDate IS NOT NULL AND u.LeaveDate < SYSUTCDATETIME() THEN 0
                            ELSE 1 END AS IsActive
                FROM dbo.AspNetUsers u
                """;
            command.CommandTimeout = 120;
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var name = await reader.IsDBNullAsync(1, ct) ? null : reader.GetString(1);
                var login = await reader.IsDBNullAsync(2, ct) ? null : reader.GetString(2);
                users.Add((reader.GetString(0), name ?? login, reader.GetInt32(3) == 1));
            }
        }

        var memberships = new List<(string, string)>();
        await using (var command = connection.CreateCommand())
        {
            // Invisible groups are permission containers rather than organisational units; including them would put
            // technical groupings next to departments in every role chart.
            command.CommandText = """
                SELECT m.UserId, g.Name
                FROM dbo.UserGroupMembers m
                JOIN dbo.UserGroups g ON g.Id = m.UserGroupId
                WHERE g.Name IS NOT NULL AND g.Visible = 1
                """;
            command.CommandTimeout = 120;
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                memberships.Add((reader.GetString(0), reader.GetString(1)));
        }

        return (users, memberships);
    }

    private async Task WriteAsync(
        List<(string Id, string? Name, bool IsActive)> users,
        List<(string UserId, string Group)> memberships,
        CancellationToken ct
    )
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        // Replace wholesale inside one transaction: a removed membership has to disappear, and a half-written
        // directory would reassign people to the wrong department for as long as it lasted.
        await ExecuteAsync(connection, "TRUNCATE dim.actor CASCADE", ct);

        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, name, isActive) in users)
        {
            var key = ActorKey(id);
            keys[id] = key;

            await using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO dim.actor (actor_key, source_id, display_name, is_active) VALUES (@k, @s, @n, @a) "
                + "ON CONFLICT (actor_key) DO NOTHING";
            command.Parameters.AddWithValue("k", key);
            command.Parameters.AddWithValue("s", id);
            command.Parameters.AddWithValue("n", (object?)name ?? DBNull.Value);
            command.Parameters.AddWithValue("a", isActive);
            await command.ExecuteNonQueryAsync(ct);
        }

        foreach (var (userId, group) in memberships)
        {
            if (!keys.TryGetValue(userId, out var key))
                continue;

            await using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO dim.actor_group (actor_key, group_name) VALUES (@k, @g) ON CONFLICT DO NOTHING";
            command.Parameters.AddWithValue("k", key);
            command.Parameters.AddWithValue("g", group);
            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// Takes the business calendar from the source: holidays with their half-day flags, and the working hours per
    /// weekday from the most recent worktime calendar.
    /// </summary>
    /// <remarks>
    /// Every duration in the product is measured in working time, so this calendar decides what "two hours" means.
    /// Maintaining it a second time here would guarantee the two drift, and a drifted calendar is invisible: the
    /// numbers stay plausible and are wrong.
    /// </remarks>
    private async Task<(int Holidays, string? Calendar)> SyncCalendarAsync(CancellationToken ct)
    {
        await using var source = new SqlConnection(_options.SourceConnectionString);
        await source.OpenAsync(ct);

        var holidays = new List<(DateTime Day, string? Name, decimal Factor)>();
        await using (var command = source.CreateCommand())
        {
            // The flags say which halves are HOLIDAY, so the factor is what is left to work: both set means the
            // whole day is off, one set means half. Read the other way round the holidays have no effect at all.
            // Confirmed against StatisticCalculationDateData.GetNumberOfHolidayDays in the source.
            //
            // the source keeps 18 holiday calendars, one per site. Loading them all into a table keyed by date meant
            // the last writer won, and a day that is a full holiday in most calendars could end up recorded as half
            //: the first run against real data reported 167 of 169 days as half days. So one calendar is chosen:
            // the one with the most entries when none is configured, which is the company-wide one in practice.
            command.CommandText = """
                SELECT e.Date, MAX(e.Name) AS Name,
                       CASE WHEN MIN(CONVERT(int, e.Forenoons)) = 1 AND MIN(CONVERT(int, e.Afternoons)) = 1 THEN 0.0
                            WHEN MAX(CONVERT(int, e.Forenoons)) = 1 OR MAX(CONVERT(int, e.Afternoons)) = 1 THEN 0.5
                            ELSE 1.0 END AS Factor
                FROM dbo.HolidayCalendarEntries e
                WHERE e.HolidayCalendarId = (
                    CASE WHEN @calendarId > 0 THEN @calendarId
                         ELSE (SELECT TOP 1 HolidayCalendarId FROM dbo.HolidayCalendarEntries
                               GROUP BY HolidayCalendarId ORDER BY COUNT(*) DESC) END)
                GROUP BY e.Date
                """;
            command.Parameters.AddWithValue("@calendarId", _options.HolidayCalendarId);
            command.CommandTimeout = 120;
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                holidays.Add(
                    (
                        reader.GetDateTime(0).Date,
                        await reader.IsDBNullAsync(1, ct) ? null : reader.GetString(1),
                        reader.GetDecimal(2)
                    )
                );
        }

        var hours = new Dictionary<int, decimal>();
        string? calendarName = null;
        await using (var command = source.CreateCommand())
        {
            // Not "the newest entry": there are 87 of them across many calendars, versioned by FromDate, and the
            // newest overall turned out to be a placeholder with nothing but zeros, which produced no working
            // hours at all and would have made every duration in the product zero.
            //
            // Instead: the current version of each calendar, ignoring the ones with no hours anywhere, and the
            // median across them. That is "a typical working day here" rather than a pretence that one calendar
            // speaks for everybody, and the screen says which it is. A specific model can be named in configuration.
            command.CommandText = """
                WITH current_version AS (
                    SELECT e.*, ROW_NUMBER() OVER (PARTITION BY e.Name ORDER BY e.FromDate DESC, e.Id DESC) AS rn
                    FROM dbo.WorktimeCalendarEntries e
                    WHERE @calendarName = '' OR e.Name = @calendarName
                ),
                usable AS (
                    SELECT * FROM current_version
                    WHERE rn = 1
                      AND (MondayHours + TuesdayHours + WednesdayHours + ThursdayHours
                           + FridayHours + SaturdayHours + SundayHours) > 0
                )
                -- COUNT(*) OVER (), not COUNT(*): a plain aggregate next to a window function makes SQL Server
                -- demand a GROUP BY over every column, and the whole statement fails.
                SELECT TOP 1 CONVERT(varchar(200), COUNT(*) OVER ()) + ' Arbeitszeitmodelle (Median)' AS Name,
                       PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY MondayHours)    OVER () AS MondayHours,
                       PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY TuesdayHours)   OVER () AS TuesdayHours,
                       PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY WednesdayHours) OVER () AS WednesdayHours,
                       PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY ThursdayHours)  OVER () AS ThursdayHours,
                       PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY FridayHours)    OVER () AS FridayHours,
                       PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY SaturdayHours)  OVER () AS SaturdayHours,
                       PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY SundayHours)    OVER () AS SundayHours
                FROM usable
                """;
            command.Parameters.AddWithValue("@calendarName", _options.WorktimeCalendarName);
            command.CommandTimeout = 120;
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                calendarName = reader.GetString(0);
                for (var day = 1; day <= 7; day++)
                    hours[day] = Convert.ToDecimal(reader.GetValue(day));
            }
        }

        await WriteCalendarAsync(holidays, hours, calendarName, ct);
        return (holidays.Count, calendarName);
    }

    private async Task WriteCalendarAsync(
        List<(DateTime Day, string? Name, decimal Factor)> holidays,
        Dictionary<int, decimal> hours,
        string? calendarName,
        CancellationToken ct
    )
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await ExecuteAsync(connection, "DELETE FROM analytics.holiday WHERE source = 'the source'", ct);
        foreach (var (day, name, factor) in holidays)
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO analytics.holiday (day, label, factor, source) VALUES (@d, @l, @f, 'the source') "
                + "ON CONFLICT (day) DO UPDATE SET label = EXCLUDED.label, factor = EXCLUDED.factor, source = 'the source'";
            command.Parameters.AddWithValue("d", day);
            command.Parameters.AddWithValue("l", (object?)name ?? DBNull.Value);
            command.Parameters.AddWithValue("f", factor);
            await command.ExecuteNonQueryAsync(ct);
        }

        if (hours.Count == 7)
        {
            // A weekday with zero hours is not a working day and must not appear as a slot at all: otherwise a
            // Saturday would silently absorb waiting time that nobody was there to work through.
            await ExecuteAsync(connection, "DELETE FROM analytics.business_slot", ct);
            foreach (var (day, dayHours) in hours.Where(pair => pair.Value > 0))
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    "INSERT INTO analytics.business_slot (dow, open_from, open_to, hours, source) "
                    + "VALUES (@d, @from, @from + make_interval(mins => (@h * 60)::int), @h, @src)";
                command.Parameters.AddWithValue("d", day);
                command.Parameters.AddWithValue(
                    "from",
                    TimeSpan.Parse(_options.DayStartsAt, CultureInfo.InvariantCulture)
                );
                command.Parameters.AddWithValue("h", dayHours);
                command.Parameters.AddWithValue("src", calendarName ?? "source");
                await command.ExecuteNonQueryAsync(ct);
            }
        }

        await transaction.CommitAsync(ct);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// The same pseudonym the projection computes. Both sides must agree or the join is empty and every role chart
    /// silently shows nothing rather than failing.
    /// </summary>
    private string ActorKey(string sourceId)
    {
        var key = string.IsNullOrWhiteSpace(_options.ActorHashKey)
            ? "local-development-actor-key"
            : _options.ActorHashKey;

        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(sourceId));
        return "a:" + Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }
}

/// <param name="Users">Users read from the directory.</param>
/// <param name="Memberships">Group memberships read.</param>
/// <param name="Holidays">Holiday entries taken over from the source.</param>
/// <param name="Calendar">Which worktime calendar the working hours came from.</param>
/// <param name="Skipped">Why nothing was read, when nothing was.</param>
public sealed record DirectoryResult(int Users, int Memberships, int Holidays, string? Calendar, string? Skipped);
