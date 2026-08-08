using KSubMaker.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KSubMaker.Infrastructure.Persistence;

/// <summary>
/// Creates the SQLite file on first run and applies any pending migration on upgrade.
///
/// <see cref="RelationalDatabaseFacadeExtensions.MigrateAsync"/> is used rather than
/// <c>EnsureCreatedAsync</c> because the latter creates a schema with no migration history, which
/// makes every future release unable to upgrade an existing installation.
/// </summary>
public sealed class DatabaseInitializer(
    IDbContextFactory<KSubMakerDbContext> contextFactory,
    IAppPaths paths,
    ILogger<DatabaseInitializer> logger) : IDatabaseInitializer
{
    private readonly IDbContextFactory<KSubMakerDbContext> _contextFactory = contextFactory;
    private readonly IAppPaths _paths = paths;
    private readonly ILogger<DatabaseInitializer> _logger = logger;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // SQLite will not create the containing folder for us.
        Directory.CreateDirectory(_paths.DatabaseDirectory);

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var pending = await db.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false);
        var pendingList = pending as IReadOnlyList<string> ?? pending.ToArray();

        if (pendingList.Count > 0)
        {
            _logger.LogInformation(
                "데이터베이스 마이그레이션 {Count}건을 적용합니다: {Migrations}",
                pendingList.Count,
                string.Join(", ", pendingList));
        }

        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);

        await EnableWriteAheadLoggingAsync(db, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("데이터베이스를 준비했습니다: {Path}", _paths.DatabaseFile);
    }

    /// <summary>
    /// WAL lets the UI read the job list while the queue pump is writing progress; in the default
    /// rollback-journal mode those two block each other and show up as "database is locked". The
    /// setting is persistent, so this is a no-op on every run after the first.
    /// </summary>
    private async Task EnableWriteAheadLoggingAsync(KSubMakerDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Some network/virtualised file systems refuse WAL. The application works without it.
            _logger.LogWarning(ex, "WAL 모드를 활성화하지 못했습니다. 기본 저널 모드로 계속합니다.");
        }
    }
}
