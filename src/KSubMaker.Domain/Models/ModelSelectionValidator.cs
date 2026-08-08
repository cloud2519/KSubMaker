using System.Text;
using KSubMaker.Domain.Hardware;
using KSubMaker.Domain.Settings;

namespace KSubMaker.Domain.Models;

/// <summary>One selected model that is not on disk, with enough context to name it to the user.</summary>
/// <param name="Kind">Which slot in the settings screen the model was chosen for.</param>
/// <param name="ModelId">The persisted id, e.g. <c>whisper-small</c>.</param>
/// <param name="DisplayName">Catalog display name, or the raw id when the catalog does not know it.</param>
public sealed record ModelSelectionIssue(ModelKind Kind, string ModelId, string DisplayName);

/// <summary>
/// One model a run will actually load, with <c>"auto"</c> already resolved.
/// </summary>
/// <param name="Kind">Which slot it fills.</param>
/// <param name="ModelId">The concrete catalog id the worker will be asked for.</param>
/// <param name="DisplayName">Catalog display name.</param>
/// <param name="ApproxSizeBytes">Download size, so the user can be told what they are agreeing to.</param>
/// <param name="IsInstalled">Whether it is already on disk.</param>
/// <param name="FromRecommendation">
/// True when the settings said <c>"auto"</c> and the hardware recommendation chose this id. Worth
/// showing: the user never picked this name and would not otherwise recognise it in an error.
/// </param>
public sealed record ModelRequirement(
    ModelKind Kind,
    string ModelId,
    string DisplayName,
    long ApproxSizeBytes,
    bool IsInstalled,
    bool FromRecommendation);

/// <summary>
/// Answers "will the models this settings screen just selected actually be there when a job runs?"
///
/// <para>Why it exists: the settings screen used to offer every model in the catalog with no hint
/// about what was installed, so picking one that had never been downloaded was a completely silent
/// action. The user found out an hour later, when a job died with
/// <c>WHISPER_MODEL_NOT_FOUND: whisper-small</c>. Nothing between the click and the failure said a
/// word.</para>
///
/// <para>Pure and side-effect free — it takes the settings, the catalog and the set of installed ids
/// and returns a verdict — so the rules are unit tested without a UI, a worker or a disk. The App
/// project is <c>net10.0-windows</c> and cannot be reached from the Linux test suite at all, which
/// is exactly why the decision lives here and only the dialog lives up there.</para>
/// </summary>
public static class ModelSelectionValidator
{
    /// <summary>The "let the app choose" sentinel. Never an issue: the resolver only picks installed models.</summary>
    public const string AutoModelId = "auto";

    /// <summary>
    /// Every selected-but-not-installed model, in settings-screen order (음성 인식 → 번역).
    /// Empty when everything the chosen configuration will load is on disk.
    /// </summary>
    /// <param name="settings">The snapshot about to be saved.</param>
    /// <param name="catalog">Used only for display names; an unknown id is still reported.</param>
    /// <param name="installedModelIds">Ids present on disk. Compared case-insensitively.</param>
    public static IReadOnlyList<ModelSelectionIssue> FindMissing(
        AppSettings settings,
        ModelCatalog catalog,
        IEnumerable<string> installedModelIds)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(installedModelIds);

        // Fake AI mode never loads a model, so warning about one would be a lie in the other
        // direction — and it is the mode people switch to precisely because nothing is downloaded.
        if (settings.FakeAiMode)
        {
            return [];
        }

        var installed = new HashSet<string>(installedModelIds, StringComparer.OrdinalIgnoreCase);
        var issues = new List<ModelSelectionIssue>(capacity: 2);

        AddIfMissing(issues, catalog, installed, ModelKind.Whisper, settings.WhisperModel);

        // Only the engine that will actually run is checked. A user who selected the LLM engine has
        // no reason to be nagged about an NLLB model they will never load.
        switch (settings.TranslationEngine)
        {
            case TranslationEngineKind.LocalTranslationModel:
                AddIfMissing(issues, catalog, installed, ModelKind.Translation, settings.TranslationModel);
                break;

            case TranslationEngineKind.LocalLlm:
                AddIfMissing(issues, catalog, installed, ModelKind.Llm, settings.LlmModel);
                break;

            case TranslationEngineKind.Fake:
            default:
                break;
        }

        return issues;
    }

    /// <summary>
    /// Every model a run under <paramref name="settings"/> will load, with <c>"auto"</c> resolved
    /// through <paramref name="recommendation"/>, in settings-screen order (음성 인식 → 번역).
    ///
    /// <para>This is the start-time counterpart to <see cref="FindMissing"/>. The two differ on
    /// purpose: <see cref="FindMissing"/> serves the settings screen, where <c>"auto"</c> means "I
    /// have not chosen" and warning about it would be noise. Here <c>"auto"</c> has to be resolved,
    /// because the run is about to happen and something concrete will be loaded either way — and
    /// leaving that resolution to the worker is what produced <c>WHISPER_MODEL_NOT_FOUND:
    /// whisper-small</c> minutes into a job, naming a model the user had never selected.</para>
    ///
    /// <para>A slot is omitted when it cannot be resolved at all (settings say <c>"auto"</c> and
    /// there is no recommendation, e.g. hardware detection failed). Nothing useful can be offered
    /// for it, and inventing a default here would repeat the defect from the other direction.</para>
    /// </summary>
    /// <param name="settings">The configuration the queue is about to run under.</param>
    /// <param name="catalog">Display names and download sizes.</param>
    /// <param name="recommendation">Hardware recommendation, or null when it is unavailable.</param>
    /// <param name="installedModelIds">Ids present on disk. Compared case-insensitively.</param>
    public static IReadOnlyList<ModelRequirement> Resolve(
        AppSettings settings,
        ModelCatalog catalog,
        HardwareRecommendation? recommendation,
        IEnumerable<string> installedModelIds)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(installedModelIds);

        if (settings.FakeAiMode)
        {
            return [];
        }

        var installed = new HashSet<string>(installedModelIds, StringComparer.OrdinalIgnoreCase);
        var requirements = new List<ModelRequirement>(capacity: 2);

        AddRequirement(
            requirements, catalog, installed,
            ModelKind.Whisper, settings.WhisperModel, recommendation?.WhisperModelId);

        switch (settings.TranslationEngine)
        {
            case TranslationEngineKind.LocalTranslationModel:
                AddRequirement(
                    requirements, catalog, installed,
                    ModelKind.Translation, settings.TranslationModel, recommendation?.TranslationModelId);
                break;

            case TranslationEngineKind.LocalLlm:
                AddRequirement(
                    requirements, catalog, installed,
                    ModelKind.Llm, settings.LlmModel, recommendation?.LlmModelId);
                break;

            case TranslationEngineKind.Fake:
            default:
                break;
        }

        return requirements;
    }

    /// <summary>
    /// The subset of <see cref="Resolve"/> that is not on disk — what a start-time prompt offers to
    /// download. Empty means the queue can start.
    /// </summary>
    public static IReadOnlyList<ModelRequirement> FindMissingToRun(
        AppSettings settings,
        ModelCatalog catalog,
        HardwareRecommendation? recommendation,
        IEnumerable<string> installedModelIds) =>
        [.. Resolve(settings, catalog, recommendation, installedModelIds).Where(r => !r.IsInstalled)];

    private static void AddRequirement(
        List<ModelRequirement> requirements,
        ModelCatalog catalog,
        HashSet<string> installed,
        ModelKind kind,
        string? configured,
        string? recommended)
    {
        var isAuto = string.IsNullOrWhiteSpace(configured) ||
                     configured.Equals(AutoModelId, StringComparison.OrdinalIgnoreCase);

        var id = (isAuto ? recommended : configured?.Trim())?.Trim();
        if (string.IsNullOrEmpty(id))
        {
            // "auto" with no recommendation to resolve it against. See the remarks on Resolve.
            return;
        }

        var descriptor = catalog.Find(id);
        if (descriptor is null || descriptor.Kind != kind)
        {
            // An id the catalog does not know cannot be downloaded, so it is not a requirement this
            // can act on. FindMissing still reports it at save time, which is where it belongs.
            return;
        }

        requirements.Add(new ModelRequirement(
            kind,
            descriptor.Id,
            descriptor.DisplayName,
            descriptor.ApproxSizeBytes,
            installed.Contains(descriptor.Id),
            FromRecommendation: isAuto));
    }

    private static void AddIfMissing(
        List<ModelSelectionIssue> issues,
        ModelCatalog catalog,
        HashSet<string> installed,
        ModelKind kind,
        string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId) ||
            modelId.Equals(AutoModelId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var id = modelId.Trim();
        if (installed.Contains(id))
        {
            return;
        }

        // An id the catalog has never heard of is reported too: it cannot be downloaded either, so
        // silently accepting it would produce the same mid-job failure with a worse message.
        issues.Add(new ModelSelectionIssue(kind, id, catalog.Find(id)?.DisplayName ?? id));
    }

    /// <summary>Korean sentence for the save-time warning. Empty string when there is nothing to say.</summary>
    public static string Describe(IReadOnlyList<ModelSelectionIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);

        if (issues.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder("선택한 모델 중 아직 내려받지 않은 것이 있습니다:");

        foreach (var issue in issues)
        {
            builder.Append(Environment.NewLine)
                   .Append(" • ")
                   .Append(DescribeKind(issue.Kind))
                   .Append(": ")
                   .Append(issue.DisplayName)
                   .Append(" (")
                   .Append(issue.ModelId)
                   .Append(')');
        }

        builder.Append(Environment.NewLine)
               .Append(Environment.NewLine)
               .Append("이대로 저장하면 작업을 시작할 때 실패합니다. 모델 관리 화면에서 먼저 내려받거나 ")
               .Append("모델을 \"자동\"으로 두세요.");

        return builder.ToString();
    }

    /// <summary>Korean name of the settings slot, used in the warning above.</summary>
    public static string DescribeKind(ModelKind kind) => kind switch
    {
        ModelKind.Whisper => "음성 인식 모델",
        ModelKind.Translation => "번역 모델",
        ModelKind.Llm => "로컬 LLM 모델",
        _ => "모델"
    };
}
