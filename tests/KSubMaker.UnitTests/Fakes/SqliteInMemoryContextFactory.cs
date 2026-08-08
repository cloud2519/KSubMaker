using KSubMaker.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace KSubMaker.UnitTests.Fakes;

/// <summary>
/// An <see cref="IDbContextFactory{TContext}"/> over a single in-memory SQLite connection.
///
/// The connection is opened once and kept open for the lifetime of the fixture: closing the last
/// connection to <c>Data Source=:memory:</c> destroys the database. This is a real SQLite engine, so
/// column types, collations and constraint behaviour are exercised for real — unlike the EF in-memory
/// provider, which would happily accept things SQLite rejects.
/// </summary>
public sealed class SqliteInMemoryContextFactory : IDbContextFactory<KSubMakerDbContext>, IDisposable
{
    private readonly SqliteConnection _connection;
    private bool _disposed;

    public SqliteInMemoryContextFactory()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        using var context = CreateDbContext();
        context.Database.EnsureCreated();
    }

    public KSubMakerDbContext CreateDbContext()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var options = new DbContextOptionsBuilder<KSubMakerDbContext>()
            .UseSqlite(_connection)
            .EnableSensitiveDataLogging()
            .Options;

        return new KSubMakerDbContext(options);
    }

    /// <summary>Writes a raw settings row, bypassing the repository, to simulate hand-edited garbage.</summary>
    public void WriteRawSetting(string key, string value)
    {
        using var context = CreateDbContext();

        var existing = context.Settings.FirstOrDefault(s => s.Key == key);
        if (existing is null)
        {
            context.Settings.Add(new SettingRecord { Key = key, Value = value });
        }
        else
        {
            existing.Value = value;
        }

        context.SaveChanges();
    }

    public IReadOnlyDictionary<string, string> ReadAllSettings()
    {
        using var context = CreateDbContext();
        return context.Settings.AsNoTracking().ToDictionary(s => s.Key, s => s.Value, StringComparer.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connection.Dispose();
    }
}
