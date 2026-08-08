using KSubMaker.Domain.Settings;

namespace KSubMaker.Domain.Models;

/// <summary>
/// Persisted record of a model that has been (or is being) installed locally.
/// Mirrors the <c>ModelInfo</c> entity requested in the specification.
/// </summary>
public sealed class ModelInstallation
{
    /// <summary>Matches <see cref="ModelDescriptor.Id"/>.</summary>
    public string Id { get; set; } = string.Empty;

    public ModelKind Type { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = "1";

    /// <summary>Absolute directory (CT2 models) or file (GGUF) on disk.</summary>
    public string? LocalPath { get; set; }

    public string? DownloadUrl { get; set; }

    /// <summary>SHA-256 of the manifest describing every constituent file (see ModelManifest).</summary>
    public string? Sha256 { get; set; }

    public long SizeBytes { get; set; }
    public bool Installed { get; set; }
    public bool Verified { get; set; }
    public long RecommendedVramBytes { get; set; }

    public DateTime? InstalledAtUtc { get; set; }
    public DateTime? VerifiedAtUtc { get; set; }

    /// <summary>Bytes already on disk for a partially completed, resumable download.</summary>
    public long DownloadedBytes { get; set; }
}

/// <summary>One file inside an installed model, with the digest it was verified against.</summary>
public sealed record ModelFileEntry(string RelativePath, long SizeBytes, string Sha256);

/// <summary>
/// Written next to a downloaded model as <c>.ksubmaker-manifest.json</c>.
/// Integrity verification is offline: it re-hashes the files on disk and compares with this manifest,
/// so it works with no network connection.
/// </summary>
public sealed record ModelManifest
{
    public required string ModelId { get; init; }
    public required string RepositoryId { get; init; }
    public required IReadOnlyList<ModelFileEntry> Files { get; init; }
    public required DateTime CreatedAtUtc { get; init; }

    public long TotalBytes => Files.Sum(f => f.SizeBytes);
}
