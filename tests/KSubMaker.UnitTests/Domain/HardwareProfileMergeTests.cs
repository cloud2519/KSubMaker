using FluentAssertions;
using KSubMaker.Domain.Hardware;
using Xunit;

namespace KSubMaker.UnitTests.Domain;

/// <summary>
/// Covers <see cref="HardwareProfile.MergeWorkerReport"/>: the point where the host's guess about
/// CUDA is replaced by the only answer that matters — the one from the process that will actually
/// load the model.
/// </summary>
public sealed class HardwareProfileMergeTests
{
    private const long Gb = 1024L * 1024L * 1024L;

    private static GpuInfo Gpu(int index, string name, double totalGb, double freeGb) => new()
    {
        Name = name,
        Index = index,
        TotalVramBytes = (long)(totalGb * Gb),
        FreeVramBytes = (long)(freeGb * Gb),
        DriverVersion = "560.94",
        ComputeCapability = "8.9"
    };

    private static HardwareProfile Local(bool cudaAvailable, params string[] warnings) => new()
    {
        Gpus = [Gpu(0, "NVIDIA GeForce RTX 4070", 12d, 11d)],
        CudaAvailable = cudaAvailable,
        CudaVersion = cudaAvailable ? "12.4" : null,
        CpuName = "Test CPU",
        LogicalCoreCount = 16,
        TotalRamBytes = 32 * Gb,
        AvailableRamBytes = 20 * Gb,
        FreeDiskBytes = 500 * Gb,
        DiskRoot = "C:\\",
        DetectionWarnings = warnings
    };

    // -----------------------------------------------------------------------
    // CUDA availability
    // -----------------------------------------------------------------------

    [Fact]
    public void The_worker_can_veto_a_host_false_positive()
    {
        // A driver is installed (nvcuda.dll loads) but CTranslate2 cannot open a device.
        var merged = Local(cudaAvailable: true).MergeWorkerReport(new WorkerHardwareReport
        {
            CudaAvailable = false,
            Warnings = ["CUDA 초기화에 실패했습니다. CPU 모드로 동작합니다."]
        });

        merged.CudaAvailable.Should().BeFalse();
        merged.DetectionWarnings.Should().Contain("CUDA 초기화에 실패했습니다. CPU 모드로 동작합니다.");
    }

    [Fact]
    public void The_worker_can_correct_a_host_false_negative()
    {
        var merged = Local(cudaAvailable: false, HardwareWarnings.CudaRuntimeNotFound)
            .MergeWorkerReport(new WorkerHardwareReport { CudaAvailable = true, CudaVersion = "12.6" });

        merged.CudaAvailable.Should().BeTrue();
        merged.CudaVersion.Should().Be("12.6");
    }

    [Fact]
    public void A_local_cuda_warning_is_retracted_once_the_worker_proves_cuda_works()
    {
        var merged = Local(
                cudaAvailable: false,
                HardwareWarnings.CudaRuntimeNotFound,
                HardwareWarnings.CudaRuntimeLoadFailed,
                "nvidia-smi가 GPU를 보고하지 않았습니다.")
            .MergeWorkerReport(new WorkerHardwareReport { CudaAvailable = true });

        merged.DetectionWarnings.Should().NotContain(HardwareWarnings.CudaRuntimeNotFound);
        merged.DetectionWarnings.Should().NotContain(HardwareWarnings.CudaRuntimeLoadFailed);

        // Unrelated warnings survive: only the ones the worker contradicted are withdrawn.
        merged.DetectionWarnings.Should().Contain("nvidia-smi가 GPU를 보고하지 않았습니다.");
    }

    // -----------------------------------------------------------------------
    // Protocol 1.2 — "the driver is fine, the libraries are not"
    // -----------------------------------------------------------------------

    [Fact]
    public void A_device_without_support_libraries_merges_as_cuda_unavailable()
    {
        // The reported machine: RTX 3080 Ti, driver CUDA 13.1, no cuBLAS 12 / cuDNN 9.
        var merged = Local(cudaAvailable: true).MergeWorkerReport(new WorkerHardwareReport
        {
            CudaAvailable = false,
            CudaDeviceDetected = true,
            CudaLibrariesAvailable = false,
            MissingCudaLibraries = ["cublas64_12.dll"]
        });

        merged.CudaAvailable.Should().BeFalse();
        merged.CudaDeviceDetected.Should().BeTrue();
        merged.CudaLibrariesAvailable.Should().BeFalse();
        merged.MissingCudaLibraries.Should().Equal("cublas64_12.dll");
        merged.CudaBlockedByMissingLibraries.Should().BeTrue();
    }

    [Fact]
    public void A_machine_with_no_gpu_at_all_is_not_blamed_on_missing_libraries()
    {
        var merged = Local(cudaAvailable: false).MergeWorkerReport(new WorkerHardwareReport
        {
            CudaAvailable = false,
            CudaDeviceDetected = false,
            CudaLibrariesAvailable = true
        });

        merged.CudaBlockedByMissingLibraries.Should().BeFalse(
            "there is nothing to accelerate, so cuBLAS is legitimately absent");
    }

    [Fact]
    public void A_report_from_a_1_1_worker_leaves_the_library_verdict_optimistic()
    {
        // WorkerHardwareReport defaults CudaLibrariesAvailable to true precisely so an older worker,
        // which cannot answer the question, does not get read as answering "no".
        var merged = Local(cudaAvailable: false)
            .MergeWorkerReport(new WorkerHardwareReport { CudaAvailable = true });

        merged.CudaAvailable.Should().BeTrue();
        merged.CudaLibrariesAvailable.Should().BeTrue();
        merged.CudaBlockedByMissingLibraries.Should().BeFalse();
    }

    [Fact]
    public void The_worker_warning_about_missing_libraries_reaches_the_settings_screen()
    {
        var warning = HardwareWarnings.CudaSupportLibrariesMissing(["cublas64_12.dll"]);

        var merged = Local(cudaAvailable: true).MergeWorkerReport(new WorkerHardwareReport
        {
            CudaAvailable = false,
            CudaDeviceDetected = true,
            CudaLibrariesAvailable = false,
            MissingCudaLibraries = ["cublas64_12.dll"],
            Warnings = [warning]
        });

        merged.DetectionWarnings.Should().Contain(warning);
        warning.Should().Contain("cublas64_12.dll").And.Contain("build-worker.ps1");
    }

    [Fact]
    public void The_missing_library_warning_falls_back_to_generic_names_when_none_are_reported()
    {
        HardwareWarnings.CudaSupportLibrariesMissing([]).Should().Contain("cuBLAS 12 / cuDNN 9");
    }

    [Fact]
    public void A_local_cuda_warning_survives_when_the_worker_agrees_there_is_no_cuda()
    {
        var merged = Local(cudaAvailable: false, HardwareWarnings.CudaRuntimeNotFound)
            .MergeWorkerReport(new WorkerHardwareReport { CudaAvailable = false });

        merged.DetectionWarnings.Should().Contain(HardwareWarnings.CudaRuntimeNotFound);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_worker_cuda_version_keeps_the_locally_detected_one(string? reported)
    {
        var merged = Local(cudaAvailable: true)
            .MergeWorkerReport(new WorkerHardwareReport { CudaAvailable = true, CudaVersion = reported });

        merged.CudaVersion.Should().Be("12.4");
    }

    // -----------------------------------------------------------------------
    // GPUs
    // -----------------------------------------------------------------------

    [Fact]
    public void Free_vram_comes_from_the_worker_while_the_static_facts_stay_local()
    {
        var merged = Local(cudaAvailable: true).MergeWorkerReport(new WorkerHardwareReport
        {
            CudaAvailable = true,
            Gpus = [Gpu(0, "GPU as the worker names it", 12d, 3.5d)]
        });

        var gpu = merged.Gpus.Should().ContainSingle().Subject;
        gpu.FreeVramBytes.Should().Be((long)(3.5d * Gb));

        // nvidia-smi already reported these on the host side; the worker only repeats them.
        gpu.Name.Should().Be("NVIDIA GeForce RTX 4070");
        gpu.TotalVramBytes.Should().Be(12 * Gb);
        gpu.DriverVersion.Should().Be("560.94");
        gpu.ComputeCapability.Should().Be("8.9");
    }

    [Fact]
    public void A_gpu_the_worker_did_not_report_keeps_its_local_numbers()
    {
        var local = Local(cudaAvailable: true) with
        {
            Gpus = [Gpu(0, "Card A", 12d, 11d), Gpu(1, "Card B", 8d, 7d)]
        };

        var merged = local.MergeWorkerReport(new WorkerHardwareReport
        {
            CudaAvailable = true,
            Gpus = [Gpu(1, "Card B", 8d, 2d)]
        });

        merged.Gpus[0].FreeVramBytes.Should().Be(11 * Gb, "index 0 was not in the worker's report");
        merged.Gpus[1].FreeVramBytes.Should().Be(2 * Gb);
    }

    [Fact]
    public void The_worker_list_is_used_when_the_host_found_no_gpu_at_all()
    {
        var local = Local(cudaAvailable: false) with { Gpus = [] };

        var merged = local.MergeWorkerReport(new WorkerHardwareReport
        {
            CudaAvailable = true,
            Gpus = [Gpu(0, "NVIDIA A2000", 6d, 5d)]
        });

        merged.Gpus.Should().ContainSingle().Which.Name.Should().Be("NVIDIA A2000");
        merged.HasNvidiaGpu.Should().BeTrue();
    }

    [Fact]
    public void An_empty_worker_gpu_list_never_erases_the_locally_detected_one()
    {
        var merged = Local(cudaAvailable: true)
            .MergeWorkerReport(new WorkerHardwareReport { CudaAvailable = true, Gpus = [] });

        merged.Gpus.Should().ContainSingle().Which.TotalVramBytes.Should().Be(12 * Gb);
    }

    // -----------------------------------------------------------------------
    // everything else
    // -----------------------------------------------------------------------

    [Fact]
    public void Cpu_ram_and_disk_are_untouched_by_the_merge()
    {
        var local = Local(cudaAvailable: true);

        var merged = local.MergeWorkerReport(new WorkerHardwareReport { CudaAvailable = true });

        merged.CpuName.Should().Be(local.CpuName);
        merged.LogicalCoreCount.Should().Be(local.LogicalCoreCount);
        merged.TotalRamBytes.Should().Be(local.TotalRamBytes);
        merged.AvailableRamBytes.Should().Be(local.AvailableRamBytes);
        merged.FreeDiskBytes.Should().Be(local.FreeDiskBytes);
        merged.DiskRoot.Should().Be(local.DiskRoot);
    }

    [Fact]
    public void Warnings_are_concatenated_local_first_and_deduplicated()
    {
        var merged = Local(cudaAvailable: false, "같은 경고", "로컬만의 경고")
            .MergeWorkerReport(new WorkerHardwareReport
            {
                CudaAvailable = false,
                Warnings = ["같은 경고", "워커만의 경고", "   "]
            });

        merged.DetectionWarnings.Should().Equal("같은 경고", "로컬만의 경고", "워커만의 경고");
    }

    [Fact]
    public void Merging_does_not_mutate_the_original_profile()
    {
        var local = Local(cudaAvailable: true);

        _ = local.MergeWorkerReport(new WorkerHardwareReport { CudaAvailable = false });

        local.CudaAvailable.Should().BeTrue();
    }

    [Fact]
    public void A_null_report_is_rejected_rather_than_silently_ignored()
    {
        var act = () => Local(cudaAvailable: true).MergeWorkerReport(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
