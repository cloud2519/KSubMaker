using System.Diagnostics;
using System.Text;
using Xunit;

namespace KSubMaker.IntegrationTests.Infrastructure;

/// <summary>
/// One-time probe for the external tools the integration suite needs.
///
/// The results are consumed by <see cref="RequiresFfmpegFactAttribute"/> and friends, which set
/// xunit's <c>Skip</c> at discovery time. That is what makes the whole class skip with a clear
/// message on a machine without ffmpeg or python instead of failing.
/// </summary>
public static class ExternalTools
{
    private static readonly Lazy<ToolStatus> FfmpegStatus = new(() => Probe("ffmpeg", "-version"), true);
    private static readonly Lazy<ToolStatus> FfprobeStatus = new(() => Probe("ffprobe", "-version"), true);
    private static readonly Lazy<ToolStatus> PythonStatus = new(() => Probe("python3", "--version"), true);
    private static readonly Lazy<ToolStatus> HuggingFaceStatus = new(ProbeHuggingFace, true);
    private static readonly Lazy<IReadOnlySet<string>> Encoders = new(ListEncoders, true);

    /// <summary>Set this to any non-empty value to keep the online catalog checks out of a run.</summary>
    public const string SkipNetworkVariable = "KSUBMAKER_SKIP_NETWORK_TESTS";

    public static bool FfmpegAvailable => FfmpegStatus.Value.Available && FfprobeStatus.Value.Available;

    public static string FfmpegSkipReason =>
        "ffmpeg/ffprobe를 찾을 수 없어 미디어 통합 테스트를 건너뜁니다. " +
        $"(ffmpeg: {FfmpegStatus.Value.Detail}, ffprobe: {FfprobeStatus.Value.Detail})";

    public static bool PythonAvailable => PythonStatus.Value.Available;

    public static string PythonSkipReason =>
        $"python3을 찾을 수 없어 worker 프로토콜 통합 테스트를 건너뜁니다. ({PythonStatus.Value.Detail})";

    /// <summary>
    /// True when huggingface.co answers. Everything else in the suite runs offline by design
    /// (ADR-020); the model catalog is the one thing that cannot be validated without asking the
    /// hub, because its whole job is to describe repositories we do not control.
    /// </summary>
    public static bool HuggingFaceAvailable => HuggingFaceStatus.Value.Available;

    public static string HuggingFaceSkipReason =>
        "huggingface.co에 연결할 수 없어 모델 카탈로그 온라인 검증을 건너뜁니다. " +
        $"({HuggingFaceStatus.Value.Detail}) 오프라인 검증은 ModelCatalogFixtureTests가 계속 수행합니다.";

    public static string FfmpegPath => "ffmpeg";

    public static string FfprobePath => "ffprobe";

    public static bool HasEncoder(string name) => Encoders.Value.Contains(name);

    /// <summary>First encoder in <paramref name="candidates"/> that this ffmpeg build supports.</summary>
    public static string PickEncoder(params string[] candidates) =>
        candidates.FirstOrDefault(HasEncoder)
        ?? throw new InvalidOperationException(
            $"이 ffmpeg 빌드에는 {string.Join(", ", candidates)} 인코더가 하나도 없습니다.");

    public static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"{executable}을(를) 시작하지 못했습니다.");

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }

            throw new TimeoutException($"{executable}이(가) {timeout.TotalSeconds:0}초 안에 끝나지 않았습니다.");
        }

        return (process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private static ToolStatus Probe(string executable, string argument)
    {
        try
        {
            var (exitCode, _, _) = RunAsync(executable, [argument], TimeSpan.FromSeconds(30))
                .GetAwaiter().GetResult();

            return exitCode == 0
                ? new ToolStatus(true, "사용 가능")
                : new ToolStatus(false, $"종료 코드 {exitCode}");
        }
        catch (Exception ex)
        {
            return new ToolStatus(false, ex.GetType().Name);
        }
    }

    /// <summary>
    /// One cheap reachability check for the whole online catalog suite, so a machine with no network
    /// pays a single short timeout instead of one per repository.
    /// </summary>
    private static ToolStatus ProbeHuggingFace()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SkipNetworkVariable)))
        {
            return new ToolStatus(false, $"{SkipNetworkVariable} 환경 변수로 비활성화됨");
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var request = new HttpRequestMessage(HttpMethod.Head, "https://huggingface.co/");
            using var response = client.Send(request);

            // A proxy that answers 403/407 is "reachable" but useless here, so it counts as offline.
            return response.IsSuccessStatusCode
                ? new ToolStatus(true, "사용 가능")
                : new ToolStatus(false, $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            return new ToolStatus(false, ex.GetType().Name);
        }
    }

    private static IReadOnlySet<string> ListEncoders()
    {
        if (!FfmpegStatus.Value.Available)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        try
        {
            var (_, stdout, _) = RunAsync("ffmpeg", ["-hide_banner", "-encoders"], TimeSpan.FromSeconds(30))
                .GetAwaiter().GetResult();

            var names = new HashSet<string>(StringComparer.Ordinal);

            foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                // " V....D libx264              libx264 H.264 / AVC ..."
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[0].Length == 6 && line.StartsWith(' '))
                {
                    names.Add(parts[1]);
                }
            }

            return names;
        }
        catch (Exception)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private readonly record struct ToolStatus(bool Available, string Detail);
}

/// <summary>A <see cref="FactAttribute"/> that skips itself when ffmpeg/ffprobe are not installed.</summary>
public sealed class RequiresFfmpegFactAttribute : FactAttribute
{
    public RequiresFfmpegFactAttribute()
    {
        if (!ExternalTools.FfmpegAvailable)
        {
            Skip = ExternalTools.FfmpegSkipReason;
        }
    }
}

/// <summary>A <see cref="TheoryAttribute"/> that skips itself when ffmpeg/ffprobe are not installed.</summary>
public sealed class RequiresFfmpegTheoryAttribute : TheoryAttribute
{
    public RequiresFfmpegTheoryAttribute()
    {
        if (!ExternalTools.FfmpegAvailable)
        {
            Skip = ExternalTools.FfmpegSkipReason;
        }
    }
}

/// <summary>A <see cref="FactAttribute"/> that skips itself when python3 is not installed.</summary>
public sealed class RequiresPythonFactAttribute : FactAttribute
{
    public RequiresPythonFactAttribute()
    {
        if (!ExternalTools.PythonAvailable)
        {
            Skip = ExternalTools.PythonSkipReason;
        }
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> that skips itself when huggingface.co cannot be reached.
///
/// The suite is offline by default (ADR-020) and must stay green on a machine with no network, so
/// this attribute — not a try/catch inside the test — is what decides whether the online model
/// catalog checks run at all.
/// </summary>
public sealed class RequiresNetworkFactAttribute : FactAttribute
{
    public RequiresNetworkFactAttribute()
    {
        if (!ExternalTools.HuggingFaceAvailable)
        {
            Skip = ExternalTools.HuggingFaceSkipReason;
        }
    }
}

/// <summary>A <see cref="TheoryAttribute"/> that skips itself when huggingface.co cannot be reached.</summary>
public sealed class RequiresNetworkTheoryAttribute : TheoryAttribute
{
    public RequiresNetworkTheoryAttribute()
    {
        if (!ExternalTools.HuggingFaceAvailable)
        {
            Skip = ExternalTools.HuggingFaceSkipReason;
        }
    }
}
