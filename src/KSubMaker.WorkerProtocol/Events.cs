using System.Text.Json.Serialization;

namespace KSubMaker.WorkerProtocol;

/// <summary>
/// Base for every worker → host message. Exactly one JSON object per stdout line.
/// Anything the worker wants to say that is not a protocol event goes to stderr.
/// </summary>
public abstract record WorkerEvent
{
    [JsonPropertyName("type")]
    public abstract string Type { get; }

    /// <summary>Echoes the originating <c>requestId</c> when the event answers a specific command.</summary>
    [JsonPropertyName("requestId")]
    public string? RequestId { get; init; }

    [JsonPropertyName("jobId")]
    public string? JobId { get; init; }
}

public sealed record ReadyEvent : WorkerEvent
{
    public override string Type => ProtocolConstants.Events.Ready;

    [JsonPropertyName("protocolVersion")]
    public string ProtocolVersion { get; init; } = ProtocolConstants.Version;

    [JsonPropertyName("workerVersion")]
    public string? WorkerVersion { get; init; }

    [JsonPropertyName("pythonVersion")]
    public string? PythonVersion { get; init; }

    [JsonPropertyName("capabilities")]
    public IReadOnlyList<string> Capabilities { get; init; } = [];
}

public sealed record AckEvent : WorkerEvent
{
    public override string Type => ProtocolConstants.Events.Ack;

    [JsonPropertyName("command")]
    public string? Command { get; init; }
}

public sealed record StartedEvent : WorkerEvent
{
    public override string Type => ProtocolConstants.Events.Started;

    [JsonPropertyName("resumedFromStage")]
    public string? ResumedFromStage { get; init; }
}

public sealed record ProgressEvent : WorkerEvent
{
    public override string Type => ProtocolConstants.Events.Progress;

    [JsonPropertyName("stage")]
    public required string Stage { get; init; }

    [JsonPropertyName("stageProgress")]
    public double StageProgress { get; init; }

    [JsonPropertyName("overallProgress")]
    public double OverallProgress { get; init; }

    /// <summary>Media seconds processed per wall-clock second.</summary>
    [JsonPropertyName("speed")]
    public double? Speed { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

public sealed record LanguageDetectedEvent : WorkerEvent
{
    public override string Type => ProtocolConstants.Events.LanguageDetected;

    [JsonPropertyName("language")]
    public required string Language { get; init; }

    [JsonPropertyName("probability")]
    public double Probability { get; init; }
}

public sealed record StageCompletedEvent : WorkerEvent
{
    public override string Type => ProtocolConstants.Events.StageCompleted;

    [JsonPropertyName("stage")]
    public required string Stage { get; init; }
}

public sealed record CompletedEvent : WorkerEvent
{
    public override string Type => ProtocolConstants.Events.Completed;

    [JsonPropertyName("outputPath")]
    public required string OutputPath { get; init; }

    [JsonPropertyName("cueCount")]
    public int CueCount { get; init; }

    [JsonPropertyName("sourceLanguage")]
    public string? SourceLanguage { get; init; }

    [JsonPropertyName("whisperModel")]
    public string? WhisperModel { get; init; }

    [JsonPropertyName("translationEngine")]
    public string? TranslationEngine { get; init; }

    [JsonPropertyName("translationModel")]
    public string? TranslationModel { get; init; }

    [JsonPropertyName("elapsedSeconds")]
    public double ElapsedSeconds { get; init; }

    /// <summary>True when the worker decided not to write because the target already existed.</summary>
    [JsonPropertyName("skipped")]
    public bool Skipped { get; init; }
}

public sealed record ErrorEvent : WorkerEvent
{
    public override string Type => ProtocolConstants.Events.Error;

    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    /// <summary>True when the host may retry (possibly after downgrading the model).</summary>
    [JsonPropertyName("recoverable")]
    public bool Recoverable { get; init; }

    /// <summary>Technical detail for the log file. Never shown verbatim in the UI.</summary>
    [JsonPropertyName("detail")]
    public string? Detail { get; init; }
}

public sealed record CancelledEvent : WorkerEvent
{
    public override string Type => ProtocolConstants.Events.Cancelled;
}

public sealed record LogEvent : WorkerEvent
{
    public override string Type => ProtocolConstants.Events.Log;

    [JsonPropertyName("level")]
    public string Level { get; init; } = "info";

    [JsonPropertyName("message")]
    public required string Message { get; init; }
}

// ---------------------------------------------------------------------------
// Hardware
// ---------------------------------------------------------------------------

public sealed record GpuDto
{
    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("totalVramBytes")]
    public long TotalVramBytes { get; init; }

    [JsonPropertyName("freeVramBytes")]
    public long FreeVramBytes { get; init; }

    [JsonPropertyName("driverVersion")]
    public string? DriverVersion { get; init; }

    [JsonPropertyName("computeCapability")]
    public string? ComputeCapability { get; init; }
}

public sealed record HardwareEvent : WorkerEvent
{
    public override string Type => ProtocolConstants.Events.Hardware;

    [JsonPropertyName("gpus")]
    public IReadOnlyList<GpuDto> Gpus { get; init; } = [];

    /// <summary>
    /// True only when a device exists <b>and</b> the CUDA support libraries load. This is the field
    /// the recommendation policy reads, and it is deliberately the conjunction: a 1.1 host that
    /// ignores the two fields below still gets a safe answer rather than an optimistic one.
    /// </summary>
    [JsonPropertyName("cudaAvailable")]
    public bool CudaAvailable { get; init; }

    /// <summary>
    /// <b>v1.2.</b> CTranslate2 could open a CUDA device — that only proves the *driver* works.
    /// Absent (false) from a 1.1 worker.
    /// </summary>
    [JsonPropertyName("cudaDeviceDetected")]
    public bool CudaDeviceDetected { get; init; }

    /// <summary>
    /// <b>v1.2.</b> cuBLAS (CUDA 12) and cuDNN 9 actually loaded. A 1.1 worker omits this; the host
    /// then treats it as true so an older worker's <c>cudaAvailable</c> is not silently downgraded.
    /// </summary>
    [JsonPropertyName("cudaLibrariesAvailable")]
    public bool CudaLibrariesAvailable { get; init; } = true;

    /// <summary><b>v1.2.</b> Support libraries that failed to load, e.g. <c>cublas64_12.dll</c>.</summary>
    [JsonPropertyName("missingCudaLibraries")]
    public IReadOnlyList<string> MissingCudaLibraries { get; init; } = [];

    [JsonPropertyName("cudaVersion")]
    public string? CudaVersion { get; init; }

    [JsonPropertyName("cpuName")]
    public string? CpuName { get; init; }

    [JsonPropertyName("logicalCores")]
    public int LogicalCores { get; init; }

    [JsonPropertyName("totalRamBytes")]
    public long TotalRamBytes { get; init; }

    [JsonPropertyName("availableRamBytes")]
    public long AvailableRamBytes { get; init; }

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

// ---------------------------------------------------------------------------
// Probe
// ---------------------------------------------------------------------------

public sealed record AudioTrackDto
{
    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("language")]
    public string? Language { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("codec")]
    public string? Codec { get; init; }

    [JsonPropertyName("channels")]
    public int Channels { get; init; }

    [JsonPropertyName("isDefault")]
    public bool IsDefault { get; init; }
}

public sealed record SubtitleTrackDto
{
    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("language")]
    public string? Language { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("codec")]
    public string? Codec { get; init; }

    [JsonPropertyName("isForced")]
    public bool IsForced { get; init; }

    [JsonPropertyName("isDefault")]
    public bool IsDefault { get; init; }
}

public sealed record ProbeResultEvent : WorkerEvent
{
    public override string Type => ProtocolConstants.Events.ProbeResult;

    [JsonPropertyName("videoPath")]
    public required string VideoPath { get; init; }

    [JsonPropertyName("durationSeconds")]
    public double DurationSeconds { get; init; }

    [JsonPropertyName("audioTracks")]
    public IReadOnlyList<AudioTrackDto> AudioTracks { get; init; } = [];

    [JsonPropertyName("subtitleTracks")]
    public IReadOnlyList<SubtitleTrackDto> SubtitleTracks { get; init; } = [];

    [JsonPropertyName("container")]
    public string? Container { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

// ---------------------------------------------------------------------------
// Models
// ---------------------------------------------------------------------------

public sealed record InstalledModelDto
{
    [JsonPropertyName("modelId")]
    public string ModelId { get; init; } = string.Empty;

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("installed")]
    public bool Installed { get; init; }

    [JsonPropertyName("verified")]
    public bool Verified { get; init; }

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; init; }

    [JsonPropertyName("downloadedBytes")]
    public long DownloadedBytes { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

public sealed record ModelListEvent : WorkerEvent
{
    public override string Type => ProtocolConstants.Events.ModelList;

    [JsonPropertyName("models")]
    public IReadOnlyList<InstalledModelDto> Models { get; init; } = [];
}

public sealed record DownloadProgressEvent : WorkerEvent
{
    public override string Type => ProtocolConstants.Events.DownloadProgress;

    [JsonPropertyName("modelId")]
    public required string ModelId { get; init; }

    [JsonPropertyName("receivedBytes")]
    public long ReceivedBytes { get; init; }

    [JsonPropertyName("totalBytes")]
    public long TotalBytes { get; init; }

    [JsonPropertyName("percent")]
    public double Percent { get; init; }

    [JsonPropertyName("currentFile")]
    public string? CurrentFile { get; init; }

    [JsonPropertyName("speedBytesPerSecond")]
    public double SpeedBytesPerSecond { get; init; }
}

public sealed record DownloadCompletedEvent : WorkerEvent
{
    public override string Type => ProtocolConstants.Events.DownloadCompleted;

    [JsonPropertyName("modelId")]
    public required string ModelId { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("verified")]
    public bool Verified { get; init; }

    [JsonPropertyName("totalBytes")]
    public long TotalBytes { get; init; }

    [JsonPropertyName("cancelled")]
    public bool Cancelled { get; init; }
}

public sealed record GoodbyeEvent : WorkerEvent
{
    public override string Type => ProtocolConstants.Events.Goodbye;
}

/// <summary>
/// Returned when a stdout line could not be understood. Never sent by the worker: the host
/// synthesises it so that one malformed line becomes a logged warning instead of a crash.
/// </summary>
public sealed record UnknownEvent : WorkerEvent
{
    public override string Type => "unknown";

    [JsonPropertyName("raw")]
    public string Raw { get; init; } = string.Empty;

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}
