using KSubMaker.Application.Abstractions;
using KSubMaker.Domain.Settings;
using Microsoft.Extensions.Logging;

namespace KSubMaker.Application.Services;

/// <summary>
/// Single owner of the live <see cref="AppSettings"/> instance.
///
/// Everything else takes a snapshot via <see cref="Current"/>; nothing mutates the object it was
/// handed. That keeps a settings change from altering the behaviour of a job that is already running.
/// </summary>
public sealed class SettingsService(
    ISettingsRepository repository,
    IAppPaths paths,
    ILogger<SettingsService> logger)
{
    private readonly ISettingsRepository _repository = repository;
    private readonly IAppPaths _paths = paths;
    private readonly ILogger<SettingsService> _logger = logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private AppSettings _current = new();

    public event EventHandler<AppSettings>? SettingsChanged;

    /// <summary>An isolated copy of the current settings.</summary>
    public AppSettings Current => _current.Clone();

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _current = await _repository.LoadAsync(cancellationToken).ConfigureAwait(false);
            ApplyPathOverrides(_current);
            return _current.Clone();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = settings.Clone();
            await _repository.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
            _current = snapshot;
            ApplyPathOverrides(_current);
            _logger.LogInformation("설정을 저장했습니다.");
        }
        finally
        {
            _lock.Release();
        }

        SettingsChanged?.Invoke(this, _current.Clone());
    }

    private void ApplyPathOverrides(AppSettings settings)
    {
        _paths.ApplyOverrides(
            string.IsNullOrWhiteSpace(settings.CacheDirectory) ? null : settings.CacheDirectory,
            string.IsNullOrWhiteSpace(settings.ModelDirectory) ? null : settings.ModelDirectory,
            string.IsNullOrWhiteSpace(settings.LogDirectory) ? null : settings.LogDirectory);

        _paths.EnsureCreated();
    }
}
