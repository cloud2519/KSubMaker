using KSubMaker.Domain.Models;
using KSubMaker.Domain.Settings;

namespace KSubMaker.Domain.Hardware;

/// <summary>The full set of automatic choices derived from a <see cref="HardwareProfile"/>.</summary>
public sealed record HardwareRecommendation
{
    public required string WhisperModelId { get; init; }
    public required ComputeType ComputeType { get; init; }
    public required string TranslationModelId { get; init; }
    public required string LlmModelId { get; init; }
    public required ProcessingStrategy Strategy { get; init; }
    public required int BeamSize { get; init; }

    /// <summary>Whether ASR and translation models can stay resident at the same time.</summary>
    public required bool CanCoResideModels { get; init; }

    public required bool UseGpu { get; init; }
    public required int MaxParallelCpuTasks { get; init; }

    /// <summary>Korean explanation shown in the settings screen.</summary>
    public required string Rationale { get; init; }
}

/// <summary>
/// Turns detected hardware into model / compute-type / strategy recommendations.
///
/// This is a *policy*, not a hard limit: every value it produces can be overridden in the settings
/// screen. It is pure and side-effect free so it can be unit tested against synthetic profiles.
/// </summary>
public static class HardwareRecommendationPolicy
{
    /// <summary>Head-room kept free so the display driver and desktop compositor do not get starved.</summary>
    private const double VramHeadroomGb = 1.0;

    public static HardwareRecommendation Recommend(HardwareProfile profile, ModelCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(catalog);

        if (!profile.HasNvidiaGpu || !profile.CudaAvailable)
        {
            return CpuFallback(profile, catalog);
        }

        var vram = profile.PrimaryVramGb;
        var (whisperId, computeType, beam) = SelectWhisper(vram);

        var translationId = vram switch
        {
            >= 12d => ModelIds.TranslationNllb13B,
            _ => ModelIds.TranslationNllb600M
        };

        // Gemma, not Qwen. Measured on a real job (측정 표본 B): Qwen2.5 7B answered 41% of the lines
        // of a Japanese file in Chinese and left another 15% untranslated, so 57% of the subtitle
        // was not Korean. Qwen is a Chinese-centric family and drifts there when the source is not
        // English; the Korean instruction in the system prompt did not hold it. The Qwen entries
        // stay in the catalog — removing an installed model from under a user is worse than leaving
        // it selectable — but nothing recommends them any more.
        //
        // 12B only above 16GB: at 12GB it cannot share the card with whisper-large-v3, and dropping
        // the whole run to 방식 B for a translation engine that is opt-in is the wrong trade.
        var llmId = vram switch
        {
            >= 16d => ModelIds.LlmGemma3_12B,
            _ => ModelIds.LlmGemma3_4B
        };

        var whisperVram = catalog.EstimatedVramGb(whisperId, computeType);
        var translationVram = catalog.EstimatedVramGb(translationId, ComputeType.Int8Float16);
        var canCoReside = (whisperVram + translationVram + VramHeadroomGb) <= vram;

        var strategy = canCoReside ? ProcessingStrategy.SequentialPerFile : ProcessingStrategy.TranscribeAllThenTranslate;

        // Only very roomy cards get the pipelined mode, where two models are hot and one job runs
        // ASR while the previous one is still translating.
        if (vram >= 16d && (whisperVram + translationVram + 2.5d) <= vram)
        {
            strategy = ProcessingStrategy.PipelinedParallel;
        }

        var rationale =
            $"{profile.PrimaryGpu!.Name} (VRAM {vram:0.#}GB) 감지됨. " +
            $"Whisper {whisperId} / {Describe(computeType)} 권장. " +
            $"번역 모델 {translationId}. " +
            (canCoReside
                ? "음성 인식과 번역 모델을 동시에 유지할 수 있어 파일 단위 순차 처리(방식 A)를 사용합니다."
                : "VRAM이 두 모델을 동시에 올리기에 부족하여 전체 음성 인식 후 번역(방식 B)을 사용합니다.");

        return new HardwareRecommendation
        {
            WhisperModelId = whisperId,
            ComputeType = computeType,
            TranslationModelId = translationId,
            LlmModelId = llmId,
            Strategy = strategy,
            BeamSize = beam,
            CanCoResideModels = canCoReside,
            UseGpu = true,
            MaxParallelCpuTasks = Math.Clamp(profile.LogicalCoreCount / 2, 1, 8),
            Rationale = rationale
        };
    }

    /// <summary>
    /// VRAM tiers. Deliberately conservative: an under-recommended model that finishes beats an
    /// over-recommended one that dies with CUDA OOM halfway through a batch of files.
    /// </summary>
    private static (string ModelId, ComputeType Compute, int BeamSize) SelectWhisper(double vramGb) => vramGb switch
    {
        >= 16d => (ModelIds.WhisperLargeV3, ComputeType.Float16, 5),
        >= 12d => (ModelIds.WhisperLargeV3, ComputeType.Float16, 5),
        >= 8d => (ModelIds.WhisperLargeV3Turbo, ComputeType.Int8Float16, 5),
        >= 6d => (ModelIds.WhisperMedium, ComputeType.Int8Float16, 5),
        >= 4d => (ModelIds.WhisperSmall, ComputeType.Int8Float16, 3),
        _ => (ModelIds.WhisperSmall, ComputeType.Int8, 1)
    };

    private static HardwareRecommendation CpuFallback(HardwareProfile profile, ModelCatalog catalog)
    {
        _ = catalog;

        var ramGb = profile.TotalRamGb;
        var modelId = ramGb >= 16d ? ModelIds.WhisperMedium : ModelIds.WhisperSmall;

        // Three distinct causes, three distinct fixes. Collapsing the middle one into
        // "CUDA를 사용할 수 없습니다" sends the user to the driver download page, which is the one
        // thing on that machine that is already correct.
        var reason = profile switch
        {
            { CudaBlockedByMissingLibraries: true } =>
                "NVIDIA GPU와 드라이버는 정상이지만 CUDA 지원 라이브러리" +
                DescribeMissingLibraries(profile) +
                "가 없습니다. scripts\\build-worker.ps1로 워커를 다시 설치하면 GPU를 쓸 수 있습니다.",
            { HasNvidiaGpu: true } => "NVIDIA GPU는 감지되었으나 CUDA를 사용할 수 없습니다.",
            _ => "NVIDIA GPU가 감지되지 않았습니다."
        };

        return new HardwareRecommendation
        {
            WhisperModelId = modelId,
            ComputeType = ComputeType.Int8,
            TranslationModelId = ModelIds.TranslationNllb600M,
            LlmModelId = ModelIds.LlmGemma3_4B,
            Strategy = ProcessingStrategy.TranscribeAllThenTranslate,
            BeamSize = 1,
            CanCoResideModels = false,
            UseGpu = false,
            MaxParallelCpuTasks = Math.Clamp(profile.LogicalCoreCount / 2, 1, 4),
            Rationale = $"{reason} CPU 모드로 동작하며, 영상 길이 대비 5~15배의 처리 시간이 걸릴 수 있습니다."
        };
    }

    /// <summary>Parenthesised list of the missing DLLs, or empty when the worker did not name any.</summary>
    private static string DescribeMissingLibraries(HardwareProfile profile)
    {
        var named = string.Join(", ", profile.MissingCudaLibraries.Where(n => !string.IsNullOrWhiteSpace(n)));
        return named.Length == 0 ? string.Empty : $"({named})";
    }

    /// <summary>
    /// Next compute type to try after a CUDA OOM, or null when already at the cheapest setting.
    /// </summary>
    public static ComputeType? Downgrade(ComputeType current) => current switch
    {
        ComputeType.Float32 => ComputeType.Float16,
        ComputeType.BFloat16 => ComputeType.Float16,
        ComputeType.Float16 => ComputeType.Int8Float16,
        ComputeType.Int8Float16 => ComputeType.Int8,
        _ => null
    };

    /// <summary>Next smaller Whisper model to try after a CUDA OOM, or null when already smallest.</summary>
    public static string? DowngradeWhisper(string modelId) => modelId switch
    {
        ModelIds.WhisperLargeV3 => ModelIds.WhisperLargeV3Turbo,
        ModelIds.WhisperLargeV3Turbo => ModelIds.WhisperMedium,
        ModelIds.WhisperMedium => ModelIds.WhisperSmall,
        ModelIds.WhisperSmall => ModelIds.WhisperBase,
        _ => null
    };

    public static string Describe(ComputeType computeType) => computeType switch
    {
        ComputeType.Float32 => "float32",
        ComputeType.Float16 => "float16",
        ComputeType.BFloat16 => "bfloat16",
        ComputeType.Int8Float16 => "int8_float16",
        ComputeType.Int8 => "int8",
        _ => "int8"
    };

    /// <summary>Parses the CTranslate2 wire name back into the enum.</summary>
    public static ComputeType Parse(string? value) => value?.ToLowerInvariant() switch
    {
        "float32" or "fp32" => ComputeType.Float32,
        "float16" or "fp16" => ComputeType.Float16,
        "bfloat16" or "bf16" => ComputeType.BFloat16,
        "int8_float16" => ComputeType.Int8Float16,
        _ => ComputeType.Int8
    };
}
