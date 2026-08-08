using KSubMaker.Application.Abstractions;
using KSubMaker.Domain.Hardware;
using KSubMaker.WorkerProtocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KSubMaker.Worker.Processing;

/// <summary>
/// Runs <c>detectHardware</c> against the Python worker and turns the reply into a
/// <see cref="WorkerHardwareReport"/>.
///
/// Everything here is best effort by design: hardware detection is an enrichment, never a
/// precondition. A worker that is down, slow, or answers with an error leaves the host on its own
/// locally-detected profile instead of failing anything the user asked for.
/// </summary>
public sealed class WorkerHardwareProbe(
    IWorkerClient client,
    IOptions<WorkerOptions> options,
    ILogger<WorkerHardwareProbe> logger) : IWorkerHardwareProbe
{
    private readonly IWorkerClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly WorkerOptions _options = options?.Value ?? new WorkerOptions();
    private readonly ILogger<WorkerHardwareProbe> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public bool IsWorkerRunning => _client.IsRunning;

    public async Task<WorkerHardwareReport?> TryDetectAsync(
        bool startWorkerIfNeeded,
        CancellationToken cancellationToken = default)
    {
        if (!_client.IsRunning)
        {
            if (!startWorkerIfNeeded)
            {
                return null;
            }

            try
            {
                await _client.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "하드웨어 확인을 위해 worker를 시작하지 못했습니다.");
                return null;
            }
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.HardwareProbeTimeout);

        try
        {
            var reply = await _client
                .RequestAsync<HardwareEvent>(new DetectHardwareCommand(), timeoutCts.Token)
                .ConfigureAwait(false);

            // The missing-library list is logged separately: "CUDA=false" on a machine with a
            // working RTX card is the exact line that sent the last investigation the wrong way.
            _logger.LogInformation(
                "worker 하드웨어 확인: CUDA={Cuda} ({Version}), 디바이스={Device}, 지원 라이브러리={Libraries}, GPU {Count}개",
                reply.CudaAvailable,
                reply.CudaVersion ?? "-",
                reply.CudaDeviceDetected,
                reply.CudaLibrariesAvailable,
                reply.Gpus.Count);

            if (reply.MissingCudaLibraries.Count > 0)
            {
                _logger.LogWarning(
                    "CUDA 지원 라이브러리를 불러오지 못했습니다: {Libraries}. GPU 작업은 실패합니다. " +
                    "scripts\\build-worker.ps1로 워커를 다시 설치하세요.",
                    string.Join(", ", reply.MissingCudaLibraries));
            }

            return Map(reply);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "worker가 {Seconds:0}초 안에 하드웨어 정보를 보고하지 않아 로컬 감지 결과를 사용합니다.",
                _options.HardwareProbeTimeout.TotalSeconds);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "worker 하드웨어 확인에 실패해 로컬 감지 결과를 사용합니다.");
            return null;
        }
    }

    private static WorkerHardwareReport Map(HardwareEvent reply) => new()
    {
        Gpus = [.. reply.Gpus.Select(gpu => new GpuInfo
        {
            Name = gpu.Name,
            Index = gpu.Index,
            TotalVramBytes = gpu.TotalVramBytes,
            FreeVramBytes = gpu.FreeVramBytes,
            DriverVersion = gpu.DriverVersion,
            ComputeCapability = gpu.ComputeCapability
        })],
        CudaAvailable = reply.CudaAvailable,
        CudaDeviceDetected = reply.CudaDeviceDetected,
        CudaLibrariesAvailable = reply.CudaLibrariesAvailable,
        MissingCudaLibraries = [.. reply.MissingCudaLibraries],
        CudaVersion = reply.CudaVersion,
        Warnings = [.. reply.Warnings]
    };
}
