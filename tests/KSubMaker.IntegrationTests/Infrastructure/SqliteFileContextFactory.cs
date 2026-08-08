using KSubMaker.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace KSubMaker.IntegrationTests.Infrastructure;

/// <summary>Lets a test reach the raw context without knowing the concrete factory type.</summary>
public interface IDbContextFactoryAccessor
{
    KSubMakerDbContext CreateDbContext();
}

/// <summary>
/// <see cref="IDbContextFactory{TContext}"/> over a real SQLite file, built the same way the
/// production container builds it (busy timeout, pooling).
///
/// A factory rather than a shared context: the queue pump writes progress from a background thread
/// while the test reads the job list, and a DbContext is explicitly not thread safe.
/// </summary>
public sealed class SqliteFileContextFactory : IDbContextFactory<KSubMakerDbContext>, IDbContextFactoryAccessor, IDisposable
{
    private readonly string _connectionString;
    private bool _disposed;

    public SqliteFileContextFactory(string databaseFile)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databaseFile)!);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databaseFile,
            DefaultTimeout = 30,
            Pooling = true
        }.ToString();

        DatabaseFile = databaseFile;
    }

    public string DatabaseFile { get; }

    public KSubMakerDbContext CreateDbContext()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var options = new DbContextOptionsBuilder<KSubMakerDbContext>()
            .UseSqlite(_connectionString)
            .Options;

        return new KSubMakerDbContext(options);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Pooled SQLite connections keep the file locked, which stops the temp directory from being
        // deleted on Windows and keeps a WAL file alive everywhere.
        SqliteConnection.ClearAllPools();
    }
}
