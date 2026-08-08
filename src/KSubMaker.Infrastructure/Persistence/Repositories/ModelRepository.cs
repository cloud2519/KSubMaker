using KSubMaker.Application.Abstractions;
using KSubMaker.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KSubMaker.Infrastructure.Persistence.Repositories;

/// <summary>
/// Tracks which models are on disk. The rows are a cache of the file system, not the truth: the
/// manifest next to the downloaded files is authoritative, which is why <c>HttpModelManager</c> can
/// rebuild this table from a directory scan.
/// </summary>
public sealed class ModelRepository(
    IDbContextFactory<KSubMakerDbContext> contextFactory,
    ILogger<ModelRepository> logger) : IModelRepository
{
    private readonly IDbContextFactory<KSubMakerDbContext> _contextFactory = contextFactory;
    private readonly ILogger<ModelRepository> _logger = logger;

    public async Task<IReadOnlyList<ModelInstallation>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        return await db.Models
            .AsNoTracking()
            .OrderBy(m => m.Type)
            .ThenBy(m => m.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ModelInstallation?> FindAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Models
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == id, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Insert or overwrite. Same detached-instance concern as <see cref="JobRepository.UpdateAsync"/>:
    /// the caller owns its object, so the tracked row's values are replaced rather than attaching the
    /// caller's graph.
    /// </summary>
    public async Task UpsertAsync(ModelInstallation model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(model.Id);

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var tracked = await db.Models.FindAsync([model.Id], cancellationToken).ConfigureAwait(false);
        if (tracked is null)
        {
            db.Models.Add(model);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            db.Entry(model).State = EntityState.Detached;
            return;
        }

        db.Entry(tracked).CurrentValues.SetValues(model);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var removed = await db.Models
            .Where(m => m.Id == id)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        if (removed == 0)
        {
            _logger.LogDebug("삭제할 모델 기록이 없습니다: {ModelId}", id);
        }
    }
}
