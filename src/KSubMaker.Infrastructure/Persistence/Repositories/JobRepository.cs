using KSubMaker.Application.Abstractions;
using KSubMaker.Domain.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KSubMaker.Infrastructure.Persistence.Repositories;

/// <summary>
/// SQLite-backed job store.
///
/// Every call opens its own short-lived <see cref="KSubMakerDbContext"/>. That is the whole reason
/// this class takes an <see cref="IDbContextFactory{TContext}"/>: the queue pump writes progress from
/// a background thread while the UI reads the list, and sharing one context between them would be a
/// data race.
/// </summary>
public sealed class JobRepository(
    IDbContextFactory<KSubMakerDbContext> contextFactory,
    ILogger<JobRepository> logger) : IJobRepository
{
    /// <summary>Statuses that mean "a crash left this job looking like it is still running".</summary>
    private static readonly JobStatus[] ActiveStatuses =
    [
        JobStatus.Probing,
        JobStatus.ExtractingAudio,
        JobStatus.Transcribing,
        JobStatus.Translating,
        JobStatus.WritingSubtitle
    ];

    private readonly IDbContextFactory<KSubMakerDbContext> _contextFactory = contextFactory;
    private readonly ILogger<JobRepository> _logger = logger;

    public async Task<IReadOnlyList<Job>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // AsNoTracking: the caller (JobQueueService) keeps its own long-lived instances and would
        // otherwise be handed entities attached to a context that is disposed on the next line.
        return await db.Jobs
            .AsNoTracking()
            .OrderBy(j => j.QueueOrder)
            .ThenBy(j => j.CreatedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Job?> FindAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Jobs
            .AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Job?> FindByPathAsync(string videoPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // The VideoPath column is declared COLLATE NOCASE, so this equality is case-insensitive and
        // still uses IX_Jobs_VideoPath.
        return await db.Jobs
            .AsNoTracking()
            .FirstOrDefaultAsync(j => j.VideoPath == videoPath, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task AddAsync(Job job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.Jobs.Add(job);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // The caller keeps using its own instance, so detach immediately: leaving it attached to a
        // disposed context is the classic source of "the instance cannot be tracked" errors later.
        db.Entry(job).State = EntityState.Detached;
    }

    public async Task AddRangeAsync(IEnumerable<Job> jobs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobs);

        var list = jobs as IReadOnlyList<Job> ?? jobs.ToArray();
        if (list.Count == 0)
        {
            return;
        }

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.Jobs.AddRange(list);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        foreach (var job in list)
        {
            db.Entry(job).State = EntityState.Detached;
        }
    }

    /// <summary>
    /// Writes a job the caller owns.
    ///
    /// The queue keeps its own <see cref="Job"/> objects alive for the whole session, so the instance
    /// handed in here is always detached and often *not* the instance that was originally read.
    /// Calling <c>Update(job)</c> would attach that graph and mark every column modified, which
    /// breaks as soon as the same id is already tracked. Instead the tracked row is loaded and its
    /// current values are overwritten with <c>SetValues</c>: EF then writes only the columns that
    /// actually changed, and the operation is safe to call repeatedly from the progress pump.
    /// </summary>
    public async Task UpdateAsync(Job job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var tracked = await db.Jobs.FindAsync([job.Id], cancellationToken).ConfigureAwait(false);
        if (tracked is null)
        {
            // The row was removed (user cleared the queue) while an in-flight progress update was
            // still on its way. Re-inserting it would resurrect a deleted job, so this is a no-op.
            _logger.LogDebug("저장할 작업이 데이터베이스에 없어 건너뜁니다: {JobId}", job.Id);
            return;
        }

        db.Entry(tracked).CurrentValues.SetValues(job);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateRangeAsync(IEnumerable<Job> jobs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobs);

        var list = jobs as IReadOnlyList<Job> ?? jobs.ToArray();
        if (list.Count == 0)
        {
            return;
        }

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var ids = list.Select(j => j.Id).ToArray();
        var tracked = await db.Jobs
            .Where(j => ids.Contains(j.Id))
            .ToDictionaryAsync(j => j.Id, cancellationToken)
            .ConfigureAwait(false);

        var missing = 0;

        foreach (var job in list)
        {
            if (tracked.TryGetValue(job.Id, out var entity))
            {
                db.Entry(entity).CurrentValues.SetValues(job);
            }
            else
            {
                missing++;
            }
        }

        if (missing > 0)
        {
            _logger.LogDebug("데이터베이스에 없는 작업 {Count}건은 저장하지 않았습니다.", missing);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Jobs.Where(j => j.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveRangeAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var list = ids as IReadOnlyList<string> ?? ids.ToArray();
        if (list.Count == 0)
        {
            return;
        }

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Jobs.Where(j => list.Contains(j.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Demotes crash-orphaned jobs to <see cref="JobStatus.Paused"/> rather than
    /// <see cref="JobStatus.Pending"/>: Paused keeps <c>CurrentStage</c> intact, so the checkpoint
    /// store can resume from the stage that was interrupted instead of re-extracting and
    /// re-transcribing from scratch. The transition goes through the domain state machine so the
    /// bookkeeping (speed, ETA) is cleared exactly as it would be for a user-initiated pause.
    /// </summary>
    public async Task<int> ResetOrphanedActiveJobsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var orphaned = await db.Jobs
            .Where(j => ActiveStatuses.Contains(j.Status))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (orphaned.Count == 0)
        {
            return 0;
        }

        foreach (var job in orphaned)
        {
            job.TransitionTo(JobStatus.Paused);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "비정상 종료로 남아 있던 작업 {Count}건을 일시중지 상태로 되돌렸습니다.", orphaned.Count);

        return orphaned.Count;
    }
}
