using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace KSubMaker.Infrastructure.Persistence;

/// <summary>
/// Used only by <c>dotnet ef</c> when scaffolding a migration.
///
/// The real connection string is built at runtime from <c>IAppPaths</c>, which needs a configured
/// host; the design-time tooling has none. It only needs a provider (to pick the SQLite migration
/// SQL generator), never an actual connection, so a throwaway path under the temp directory is
/// enough — no file is created by <c>migrations add</c>.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<KSubMakerDbContext>
{
    public KSubMakerDbContext CreateDbContext(string[] args)
    {
        var designTimePath = Path.Combine(Path.GetTempPath(), "ksubmaker-design-time.db");

        var options = new DbContextOptionsBuilder<KSubMakerDbContext>()
            .UseSqlite($"Data Source={designTimePath}")
            .Options;

        return new KSubMakerDbContext(options);
    }
}
