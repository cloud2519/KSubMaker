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

/// <summary>
/// Where a job's source text comes from when the video has subtitles available.
///
/// <para>Split out of the old single <c>ExistingSubtitlePolicy</c>, which answered two unrelated
/// questions at once — "should this file be processed at all" and "what should be translated" — so
/// that perfectly reasonable combinations ("translate the sidecar, but skip files that already have
/// Korean") could not be expressed. That one is now <see cref="ExistingSubtitleRule"/>.</para>
/// </summary>
public enum SubtitleSourcePreference
{
    /// <summary>Always transcribe the audio, whatever else exists. (MVP core path, and the default.)</summary>
    AudioOnly,

    /// <summary>
    /// Translate a sidecar file (<c>movie.ja.srt</c>) when there is a usable one, otherwise
    /// transcribe. Which sidecar is decided by <c>ExternalSubtitleSelector</c>.
    /// </summary>
    PreferExternalFile,

    /// <summary>Translate an embedded subtitle track when there is one, otherwise transcribe.</summary>
    PreferEmbeddedTrack,

    /// <summary>
    /// Translate whatever subtitle is available, otherwise transcribe. A sidecar wins over an
    /// embedded track: it is plain text we can read directly, while a track has to be demuxed and
    /// may turn out to be a bitmap format we cannot use.
    /// </summary>
    PreferAnySubtitle,

    /// <summary>Ask per file, once the queue knows what each file actually contains.</summary>
    AskPerFile
}

/// <summary>
/// What to do with a file that already has a subtitle — the "should we process this at all"
/// question, kept apart from <see cref="SubtitleSourcePreference"/>.
/// </summary>
public enum ExistingSubtitleRule
{
    /// <summary>
    /// A Korean subtitle means the work is done; mark the file complete without processing it.
    /// The default, and what the old standalone <c>SkipIfKoreanSubtitleExists</c> checkbox did.
    /// </summary>
    CompleteIfKoreanExists,

    /// <summary>Skip the file when any same-named sidecar exists, whatever language it is in.</summary>
    SkipIfAnySubtitleExists,

    /// <summary>Process the file regardless of what already sits next to it.</summary>
    ProcessAnyway
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
