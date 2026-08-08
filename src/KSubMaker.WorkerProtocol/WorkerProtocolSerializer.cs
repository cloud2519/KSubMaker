using System.Text.Json;
using System.Text.Json.Serialization;

namespace KSubMaker.WorkerProtocol;

/// <summary>
/// JSON Lines codec for the host ⇄ worker channel.
///
/// Deliberately hand-rolled discriminator dispatch instead of <c>[JsonPolymorphic]</c>: the worker is
/// written in Python and cannot be relied upon to emit the discriminator as the first property, which
/// is what System.Text.Json's built-in polymorphism requires.
/// </summary>
public static class WorkerProtocolSerializer
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Serialises a command to a single line. The runtime type is passed explicitly so that
    /// derived-type properties are written even when the static type is <see cref="WorkerCommand"/>.
    /// </summary>
    public static string SerializeCommand(WorkerCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return JsonSerializer.Serialize(command, command.GetType(), Options);
    }

    public static string SerializeEvent(WorkerEvent workerEvent)
    {
        ArgumentNullException.ThrowIfNull(workerEvent);
        return JsonSerializer.Serialize(workerEvent, workerEvent.GetType(), Options);
    }

    /// <summary>
    /// Parses one stdout line. Never throws: an unparseable or unknown line becomes an
    /// <see cref="UnknownEvent"/> so a single bad line cannot take the pipeline down.
    /// </summary>
    public static WorkerEvent DeserializeEvent(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return new UnknownEvent { Raw = line ?? string.Empty, Reason = "빈 줄" };
        }

        var trimmed = line.Trim();

        // Cheap guard so that stray non-JSON output (a Python warning that escaped to stdout, a
        // progress bar, a BOM) is discarded before the parser is even invoked.
        if (trimmed[0] != '{')
        {
            return new UnknownEvent { Raw = trimmed, Reason = "JSON 객체가 아닙니다" };
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new UnknownEvent { Raw = trimmed, Reason = "JSON 객체가 아닙니다" };
            }

            if (!document.RootElement.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String)
            {
                return new UnknownEvent { Raw = trimmed, Reason = "type 필드가 없습니다" };
            }

            var type = typeElement.GetString();
            var clrType = ResolveEventType(type);

            if (clrType is null)
            {
                return new UnknownEvent { Raw = trimmed, Reason = $"알 수 없는 이벤트 유형: {type}" };
            }

            var result = (WorkerEvent?)JsonSerializer.Deserialize(trimmed, clrType, Options);
            return result ?? new UnknownEvent { Raw = trimmed, Reason = "역직렬화 결과가 null입니다" };
        }
        catch (JsonException ex)
        {
            return new UnknownEvent { Raw = trimmed, Reason = $"JSON 구문 오류: {ex.Message}" };
        }
        catch (Exception ex)
        {
            return new UnknownEvent { Raw = trimmed, Reason = $"처리 실패: {ex.Message}" };
        }
    }

    /// <summary>Parses a host → worker line. Used by the protocol round-trip tests and by fake workers.</summary>
    public static WorkerCommand? DeserializeCommand(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var trimmed = line.Trim();
        if (trimmed[0] != '{')
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);

            if (!document.RootElement.TryGetProperty("command", out var commandElement) ||
                commandElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var clrType = ResolveCommandType(commandElement.GetString());
            return clrType is null ? null : (WorkerCommand?)JsonSerializer.Deserialize(trimmed, clrType, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Type? ResolveEventType(string? type) => type switch
    {
        ProtocolConstants.Events.Ready => typeof(ReadyEvent),
        ProtocolConstants.Events.Ack => typeof(AckEvent),
        ProtocolConstants.Events.Started => typeof(StartedEvent),
        ProtocolConstants.Events.Progress => typeof(ProgressEvent),
        ProtocolConstants.Events.LanguageDetected => typeof(LanguageDetectedEvent),
        ProtocolConstants.Events.StageCompleted => typeof(StageCompletedEvent),
        ProtocolConstants.Events.Completed => typeof(CompletedEvent),
        ProtocolConstants.Events.Error => typeof(ErrorEvent),
        ProtocolConstants.Events.Cancelled => typeof(CancelledEvent),
        ProtocolConstants.Events.Log => typeof(LogEvent),
        ProtocolConstants.Events.Hardware => typeof(HardwareEvent),
        ProtocolConstants.Events.ProbeResult => typeof(ProbeResultEvent),
        ProtocolConstants.Events.ModelList => typeof(ModelListEvent),
        ProtocolConstants.Events.DownloadProgress => typeof(DownloadProgressEvent),
        ProtocolConstants.Events.DownloadCompleted => typeof(DownloadCompletedEvent),
        ProtocolConstants.Events.Goodbye => typeof(GoodbyeEvent),
        _ => null
    };

    private static Type? ResolveCommandType(string? command) => command switch
    {
        ProtocolConstants.Commands.Hello => typeof(HelloCommand),
        ProtocolConstants.Commands.DetectHardware => typeof(DetectHardwareCommand),
        ProtocolConstants.Commands.Probe => typeof(ProbeCommand),
        ProtocolConstants.Commands.Process => typeof(ProcessCommand),
        ProtocolConstants.Commands.ExtractAudio => typeof(ExtractAudioCommand),
        ProtocolConstants.Commands.Cancel => typeof(CancelCommand),
        ProtocolConstants.Commands.ListModels => typeof(ListModelsCommand),
        ProtocolConstants.Commands.DownloadModel => typeof(DownloadModelCommand),
        ProtocolConstants.Commands.CancelDownload => typeof(CancelDownloadCommand),
        ProtocolConstants.Commands.VerifyModel => typeof(VerifyModelCommand),
        ProtocolConstants.Commands.DeleteModel => typeof(DeleteModelCommand),
        ProtocolConstants.Commands.Shutdown => typeof(ShutdownCommand),
        _ => null
    };

    /// <summary>
    /// Compares the worker's protocol version with <see cref="ProtocolConstants.Version"/>.
    /// A different major version is incompatible; a different minor version is tolerated.
    /// </summary>
    public static bool IsCompatible(string? workerVersion, out string? warning)
    {
        warning = null;

        if (string.IsNullOrWhiteSpace(workerVersion))
        {
            warning = "Worker가 프로토콜 버전을 보고하지 않았습니다.";
            return false;
        }

        var hostParts = ProtocolConstants.Version.Split('.');
        var workerParts = workerVersion.Split('.');

        if (workerParts.Length == 0 || hostParts[0] != workerParts[0])
        {
            warning = $"프로토콜 버전이 호환되지 않습니다. 호스트 {ProtocolConstants.Version}, Worker {workerVersion}.";
            return false;
        }

        if (workerVersion != ProtocolConstants.Version)
        {
            warning = $"프로토콜 부 버전이 다릅니다. 호스트 {ProtocolConstants.Version}, Worker {workerVersion}.";
        }

        return true;
    }
}
