using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProcessAnalyzer.Web.Data;
using ProcessAnalyzer.Web.Options;
using ProcessAnalyzer.Web.Vocabulary;
using Testcontainers.PostgreSql;

namespace ProcessAnalyzer.Tests;

/// <summary>
/// One PostgreSQL container for the whole collection. Starting a container per test class would
/// add roughly a second each and buy nothing — the tests isolate themselves by truncating.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    // The credentials are container-local and thrown away with the container. Nothing here is a
    // secret, and nothing here may ever be copied into a real connection string.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("process")
        .Build();

    public IDbContextFactory<AppDbContext> Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_container.GetConnectionString()).Options;
        Factory = new PostgresDbContextFactory(options);

        // The schema comes from the EF migration, not from a hand-written script in the test
        // project. A test schema that drifts from the migration proves nothing about production.
        // The migration itself is generated in the build step.
        await using var db = await Factory.CreateDbContextAsync(CancellationToken.None);
        await db.Database.MigrateAsync(CancellationToken.None);

        // Labels, the payload allowlist and the discriminator rules arrive the same way they do in production: from
        // the vocabulary, after the migrations. Seeding them from a script in the test project instead would prove
        // the screens work against rows no deployment ever has.
        //
        // The examples, and only the examples: asserting a rendered German sentence needs known input, so these tests
        // must not depend on which vocabulary the machine happens to carry. Loading a second one would also break the
        // rule that no two event types share a label — correctly, because two vocabularies describe two sources.
        // Whether an installation's own vocabulary is complete is a question about files, and LabelCoverageTests
        // answers it without a database.
        await new VocabularyLoader(
            Factory,
            new ProcessAnalyzerOptions { VocabularyPath = TestVocabulary.ExampleDirectory },
            NullLogger<VocabularyLoader>.Instance
        ).LoadAsync(CancellationToken.None);
    }

    /// <summary>
    /// Empties every table between tests. RESTART IDENTITY matters: sync.run ids are asserted on,
    /// and a leftover sequence value would make those assertions depend on execution order.
    /// </summary>
    public async Task ResetAsync()
    {
        await using var db = await Factory.CreateDbContextAsync(CancellationToken.None);
        await db.Database.ExecuteSqlRawAsync(
            // The derived tables go too. A test that seeds ocel.* directly would otherwise leave rows behind and the
            // next test would measure them without ever having written them.
            "TRUNCATE journal.event_object, journal.event, sync.run, sync.cursor, "
                + "ocel.e2o, ocel.event, ocel.object, ocel.type_registry RESTART IDENTITY CASCADE"
        );
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    private sealed class PostgresDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;

        public PostgresDbContextFactory(DbContextOptions<AppDbContext> options)
        {
            _options = options;
        }

        public AppDbContext CreateDbContext() => new(_options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
