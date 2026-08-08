using FluentAssertions;
using KSubMaker.Application.Abstractions;
using KSubMaker.Application.Services;
using KSubMaker.Domain.Hardware;
using KSubMaker.Domain.Models;
using KSubMaker.Domain.Settings;
using KSubMaker.UnitTests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KSubMaker.UnitTests.Application;

/// <summary>
/// The two-stage detection contract: local first (cheap, always), the Python worker's authoritative
/// CUDA answer second (only when it is free, or when the user explicitly asks).
/// </summary>
public sealed class HardwareServiceTests
{
    private const long Gb = 1024L * 1024L * 1024L;

    private static readonly ModelCatalog Catalog = new();

    // -----------------------------------------------------------------------
    // fakes
    // -----------------------------------------------------------------------

    private sealed class FakeDetector(HardwareProfile profile) : IHardwareDetector
    {
        public int Calls { get; private set; }

        public Task<HardwareProfile> DetectAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(profile);
        }
    }

    private sealed class FakeWorkerProbe(WorkerHardwareReport? report) : IWorkerHardwareProbe
    {
        public bool IsWorkerRunning { get; set; }

        public int Calls { get; private set; }

        public List<bool> StartRequests { get; } = [];

        public Task<WorkerHardwareReport?> TryDetectAsync(
            bool startWorkerIfNeeded,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            StartRequests.Add(startWorkerIfNeeded);

            // The real probe returns null rather than starting the process it was told not to start.
            if (!IsWorkerRunning && !startWorkerIfNeeded)
            {
                return Task.FromResult<WorkerHardwareReport?>(null);
            }

            return Task.FromResult(report);
        }
    }

    private static HardwareProfile LocalProfile(bool cudaAvailable, double vramGb = 12d) => new()
    {
        Gpus =
        [
            new GpuInfo
            {
                Name = "NVIDIA GeForce RTX 4070",
                Index = 0,
                TotalVramBytes = (long)(vramGb * Gb),
                FreeVramBytes = (long)(vramGb * Gb)
            }
        ],
        CudaAvailable = cudaAvailable,
        CudaVersion = cudaAvailable ? "12.4" : null,
        CpuName = "Test CPU",
        LogicalCoreCount = 16,
        TotalRamBytes = 32 * Gb,
        DetectionWarnings = cudaAvailable ? [] : [HardwareWarnings.CudaRuntimeNotFound]
    };

    private static HardwareService NewService(
        IHardwareDetector detector,
        IWorkerHardwareProbe? probe = null,
        ILogger<HardwareService>? logger = null) =>
        new(detector, Catalog, logger ?? NullLogger<HardwareService>.Instance, probe);

    // -----------------------------------------------------------------------
    // start-up must not spawn a Python process
    // -----------------------------------------------------------------------

    [Fact]
    public async Task The_first_detection_does_not_start_the_worker()
    {
        var probe = new FakeWorkerProbe(new WorkerHardwareReport { CudaAvailable = true })
        {
            IsWorkerRunning = false
        };

        var service = NewService(new FakeDetector(LocalProfile(cudaAvailable: false)), probe);

        await service.GetProfileAsync();

        probe.Calls.Should().Be(0, "starting CPython before the user has done anything is the trade this avoids");
        service.HasWorkerAnswer.Should().BeFalse();
        service.CurrentProfile.CudaAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task A_worker_that_is_already_running_is_asked_during_an_ordinary_refresh()
    {
        var probe = new FakeWorkerProbe(new WorkerHardwareReport { CudaAvailable = true, CudaVersion = "12.6" })
        {
            IsWorkerRunning = true
        };

        var service = NewService(new FakeDetector(LocalProfile(cudaAvailable: false)), probe);

        await service.RefreshAsync();

        probe.StartRequests.Should().Equal(false);
        service.CurrentProfile.CudaAvailable.Should().BeTrue();
        service.CurrentProfile.CudaVersion.Should().Be("12.6");
        service.HasWorkerAnswer.Should().BeTrue();
    }

    [Fact]
    public async Task An_explicit_refresh_may_start_the_worker()
    {
        var probe = new FakeWorkerProbe(new WorkerHardwareReport { CudaAvailable = false })
        {
            IsWorkerRunning = false
        };

        var service = NewService(new FakeDetector(LocalProfile(cudaAvailable: true)), probe);

        await service.RefreshAsync(HardwareRefreshMode.IncludeWorker);

        probe.StartRequests.Should().Equal(true);
        service.CurrentProfile.CudaAvailable.Should().BeFalse("the worker is authoritative");
    }

    // -----------------------------------------------------------------------
    // the deferred merge
    // -----------------------------------------------------------------------

    [Fact]
    public async Task The_profile_picks_up_the_worker_answer_once_the_worker_is_up()
    {
        var probe = new FakeWorkerProbe(new WorkerHardwareReport
        {
            CudaAvailable = false,
            Warnings = ["CUDA 초기화에 실패했습니다. CPU 모드로 동작합니다."]
        });

        var service = NewService(new FakeDetector(LocalProfile(cudaAvailable: true)), probe);

        var raised = new List<HardwareProfile>();
        service.ProfileChanged += (_, profile) => raised.Add(profile);

        await service.GetProfileAsync();
        service.CurrentProfile.CudaAvailable.Should().BeTrue("only the local guess is available yet");

        // The worker has now come up for a job.
        probe.IsWorkerRunning = true;
        var merged = await service.RefreshFromWorkerAsync();

        merged.Should().BeTrue();
        service.CurrentProfile.CudaAvailable.Should().BeFalse();
        service.CurrentProfile.DetectionWarnings.Should().Contain("CUDA 초기화에 실패했습니다. CPU 모드로 동작합니다.");
        raised.Should().HaveCount(2, "the UI has to be told the answer changed");
        raised[^1].CudaAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task The_recommendation_is_recomputed_from_the_merged_profile()
    {
        var probe = new FakeWorkerProbe(new WorkerHardwareReport { CudaAvailable = false });
        var service = NewService(new FakeDetector(LocalProfile(cudaAvailable: true, vramGb: 24d)), probe);

        var before = await service.GetRecommendationAsync();
        before.UseGpu.Should().BeTrue();
        before.WhisperModelId.Should().Be(ModelIds.WhisperLargeV3);

        probe.IsWorkerRunning = true;
        await service.RefreshFromWorkerAsync();

        var after = await service.GetRecommendationAsync();
        after.UseGpu.Should().BeFalse("a 24GB card is useless if CTranslate2 cannot open it");
        after.ComputeType.Should().Be(ComputeType.Int8);
        after.Strategy.Should().Be(ProcessingStrategy.TranscribeAllThenTranslate);
    }

    [Fact]
    public async Task The_worker_is_only_asked_once_per_detection()
    {
        var probe = new FakeWorkerProbe(new WorkerHardwareReport { CudaAvailable = true })
        {
            IsWorkerRunning = true
        };

        var service = NewService(new FakeDetector(LocalProfile(cudaAvailable: false)), probe);
        await service.GetProfileAsync();

        (await service.RefreshFromWorkerAsync()).Should().BeFalse();
        (await service.RefreshFromWorkerAsync()).Should().BeFalse();

        probe.Calls.Should().Be(1, "the answer was already folded in by the initial detection");
    }

    [Fact]
    public async Task A_local_re_detection_makes_the_worker_answer_stale_again()
    {
        var probe = new FakeWorkerProbe(new WorkerHardwareReport { CudaAvailable = true });
        var service = NewService(new FakeDetector(LocalProfile(cudaAvailable: false)), probe);

        await service.GetProfileAsync();
        probe.IsWorkerRunning = true;
        await service.RefreshFromWorkerAsync();
        service.HasWorkerAnswer.Should().BeTrue();

        probe.IsWorkerRunning = false;
        await service.RefreshAsync();

        service.HasWorkerAnswer.Should().BeFalse();
        service.CurrentProfile.CudaAvailable.Should().BeFalse("the merged answer belonged to the previous probe");
    }

    [Fact]
    public async Task Nothing_is_probed_before_the_first_local_detection()
    {
        var probe = new FakeWorkerProbe(new WorkerHardwareReport { CudaAvailable = true })
        {
            IsWorkerRunning = true
        };

        var service = NewService(new FakeDetector(LocalProfile(cudaAvailable: false)), probe);

        (await service.RefreshFromWorkerAsync()).Should().BeFalse();
        probe.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_worker_that_cannot_answer_leaves_the_local_profile_alone()
    {
        var probe = new FakeWorkerProbe(report: null) { IsWorkerRunning = true };
        var service = NewService(new FakeDetector(LocalProfile(cudaAvailable: true)), probe);

        await service.RefreshAsync();

        service.CurrentProfile.CudaAvailable.Should().BeTrue();
        service.HasWorkerAnswer.Should().BeFalse();
    }

    [Fact]
    public async Task Everything_still_works_without_a_worker_probe_at_all()
    {
        var service = NewService(new FakeDetector(LocalProfile(cudaAvailable: true)));

        var profile = await service.GetProfileAsync();

        profile.CudaAvailable.Should().BeTrue();
        (await service.RefreshFromWorkerAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task The_merge_is_reported_in_the_log_so_a_support_log_shows_which_answer_was_used()
    {
        var logger = new CapturingLogger<HardwareService>();
        var probe = new FakeWorkerProbe(new WorkerHardwareReport { CudaAvailable = false })
        {
            IsWorkerRunning = true
        };

        var service = NewService(new FakeDetector(LocalProfile(cudaAvailable: true)), probe, logger);
        await service.RefreshAsync();

        logger.ContainsMessage("worker 확인 포함").Should().BeTrue();
    }

    // -----------------------------------------------------------------------
    // composition
    // -----------------------------------------------------------------------

    /// <summary>
    /// Infrastructure registers <see cref="HardwareService"/>; the probe only exists once the worker
    /// layer has registered it. Resolution must therefore work with the parameter absent.
    /// </summary>
    [Fact]
    public void The_service_resolves_without_a_registered_worker_probe()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Catalog);
        services.AddSingleton<IHardwareDetector>(new FakeDetector(LocalProfile(cudaAvailable: false)));
        services.AddSingleton<HardwareService>();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<HardwareService>().Should().NotBeNull();
    }

    [Fact]
    public async Task The_service_uses_a_registered_worker_probe()
    {
        var probe = new FakeWorkerProbe(new WorkerHardwareReport { CudaAvailable = true }) { IsWorkerRunning = true };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Catalog);
        services.AddSingleton<IHardwareDetector>(new FakeDetector(LocalProfile(cudaAvailable: false)));
        services.AddSingleton<IWorkerHardwareProbe>(probe);
        services.AddSingleton<HardwareService>();

        using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<HardwareService>().RefreshAsync();

        probe.Calls.Should().Be(1);
    }
}
