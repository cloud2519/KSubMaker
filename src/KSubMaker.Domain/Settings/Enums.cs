namespace KSubMaker.Domain.Settings;

/// <summary>Which translation backend converts the source transcript into Korean.</summary>
public enum TranslationEngineKind
{
    /// <summary>Dedicated neural MT model (CTranslate2 / NLLB). Default: fast, deterministic, small.</summary>
    LocalTranslationModel,

    /// <summary>Local instruction-tuned LLM through the bundled llama.cpp server. Better style control.</summary>
    LocalLlm,

    /// <summary>Deterministic in-process fake used by tests and by the "Fake AI" diagnostic mode.</summary>
    Fake
}

/// <summary>CTranslate2 compute types, ordered from most to least memory hungry.</summary>
public enum ComputeType
{
    Float32,
    Float16,
    BFloat16,
    Int8Float16,
    Int8
}

/// <summary>How the queue schedules ASR and translation across the GPU.</summary>
public enum ProcessingStrategy
{
    /// <summary>Pick A/B/C from detected hardware. Default.</summary>
    Auto,

    /// <summary>Per file: extract → transcribe → translate → write, then next file.</summary>
    SequentialPerFile,

    /// <summary>Transcribe every file first, unload Whisper, then translate everything.</summary>
    TranscribeAllThenTranslate,

    /// <summary>Keep both models resident and overlap transcription of file N+1 with translation of file N.</summary>
    PipelinedParallel
}

/// <summary>What to do when the source video already has subtitles.</summary>
public enum ExistingSubtitlePolicy
{
    /// <summary>Ignore everything that exists and always transcribe the audio. (MVP core path.)</summary>
    AlwaysTranscribe,

    /// <summary>Skip the file when a sidecar subtitle with the same base name exists.</summary>
    SkipIfExternalSubtitleExists,

    /// <summary>Extract an embedded subtitle track and translate it instead of running ASR.</summary>
    UseEmbeddedTrack,

    /// <summary>
    /// Translate a sidecar file (<c>movie.ja.srt</c>) instead of running ASR, when one exists.
    /// Which sidecar is decided by <c>ExternalSubtitleSelector</c>; a file with none falls back to
    /// the audio path rather than being skipped.
    /// </summary>
    UseExternalSubtitle,

    /// <summary>Treat the file as done when a Korean subtitle already exists.</summary>
    CompleteIfKoreanExists,

    /// <summary>Ask per file in the queue.</summary>
    AskPerFile
}

/// <summary>What to do when the target <c>*.ko.srt</c> already exists.</summary>
public enum OutputConflictPolicy
{
    /// <summary>Default. Leave the existing file alone and mark the job completed.</summary>
    Skip,
    Overwrite,
    CreateNumberedCopy
}

/// <summary>Tone and register applied by the translation prompt / MT post-rules.</summary>
public enum TranslationStyle
{
    /// <summary>자연스러운 한국어 (default)</summary>
    Natural,

    /// <summary>직역에 가까운 번역</summary>
    Literal,

    /// <summary>존댓말 우선</summary>
    Polite,

    /// <summary>반말 유지</summary>
    Casual,

    /// <summary>원문 말투 유지</summary>
    PreserveSourceRegister
}

/// <summary>Where a model comes from and what loads it.</summary>
public enum ModelKind
{
    Whisper,
    Translation,
    Llm
}
