using KSubMaker.Domain.Models;
using KSubMaker.Domain.Settings;

namespace KSubMaker.Application.Abstractions;

public sealed record ModelDownloadProgress
{
    public required string ModelId { get; init; }
    public long ReceivedBytes { get; init; }
    public long TotalBytes { get; init; }
    public double Percent => TotalBytes <= 0 ? 0d : Math.Clamp(ReceivedBytes * 100d / TotalBytes, 0d, 100d);
    public string? CurrentFile { get; init; }
    public double SpeedBytesPerSecond { get; init; }
}

/// <summary>Catalog entry joined with whatever is on disk.</summary>
public sealed record ModelStatus
{
    public required ModelDescriptor Descriptor { get; init; }
    public required ModelInstallation Installation { get; init; }
    public bool IsDownloading { get; init; }
    public double DownloadPercent { get; init; }

    public double EstimatedVramGb { get; init; }
    public bool IsRecommended { get; init; }
}

/// <summary>
/// Download / verify / delete for model payloads.
///
/// Downloads are resumable: each file is fetched with an HTTP Range request into <c>*.part</c> and
/// only renamed into place once its SHA-256 matches the digest published by the repository.
/// </summary>
public interface IModelManager
{
    Task<IReadOnlyList<ModelStatus>> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<ModelStatus?> GetStatusAsync(string modelId, CancellationToken cancellationToken = default);

    /// <summary>Downloads (or resumes) a model. Progress is reported per model, not per file.</summary>
    Task DownloadAsync(
        string modelId,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken);

    /// <summary>Re-hashes the installed files against the stored manifest. Works offline.</summary>
    Task<bool> VerifyAsync(string modelId, CancellationToken cancellationToken = default);

    Task DeleteAsync(string modelId, CancellationToken cancellationToken = default);

    /// <summary>True when every file of the model is present and the manifest exists.</summary>
    Task<bool> IsInstalledAsync(string modelId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves "auto" to a concrete model id using the current hardware recommendation, falling back
    /// to the largest installed model of the right kind when the recommended one is not installed.
    /// </summary>
    Task<string?> ResolveModelIdAsync(string requested, ModelKind kind, CancellationToken cancellationToken = default);
}
