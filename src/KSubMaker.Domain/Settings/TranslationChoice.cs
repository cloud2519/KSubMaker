using KSubMaker.Domain.Models;

namespace KSubMaker.Domain.Settings;

/// <summary>
/// The settings screen offers **one** list of translation models; the engine follows from whichever
/// one is picked.
///
/// <para>Why: the engine and the model used to be two independent controls, so "번역 모델" could
/// name an LLM while "번역 엔진" still said 전용 번역 모델 — a combination that reads as configured
/// and does nothing, because the run only looks at the engine. The same defect reached the model
/// screen, where finishing a download set <c>LlmModel</c> and left the engine alone, so the model
/// the user had just been asked about never ran.</para>
///
/// <para><see cref="AppSettings"/> keeps its three fields and the worker protocol is untouched.
/// This type is only the mapping between that triple and the single thing a person chooses, which
/// keeps the change inside the UI instead of spreading to the wire, the database and the settings
/// fingerprints.</para>
/// </summary>
public static class TranslationChoice
{
    /// <summary>"자동" for the dedicated engine: the hardware-recommended NLLB model.</summary>
    public const string AutoTranslationId = "auto";

    /// <summary>
    /// "자동" for the LLM engine. A separate sentinel because one <c>"auto"</c> cannot express both
    /// engines, and <c>engine = LocalLlm, LlmModel = "auto"</c> is the state today's settings screen
    /// produces by default — collapsing it would silently rewrite an existing user's configuration
    /// the first time they opened the window.
    /// </summary>
    public const string AutoLlmId = "llm-auto";

    /// <summary>The deterministic fake engine. Not a catalog id: it loads no model at all.</summary>
    public const string FakeId = "fake-engine";

    /// <summary>The single id that represents <paramref name="settings"/>' translation choice.</summary>
    public static string Selected(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return settings.TranslationEngine switch
        {
            TranslationEngineKind.Fake => FakeId,
            TranslationEngineKind.LocalLlm => IsAuto(settings.LlmModel) ? AutoLlmId : settings.LlmModel.Trim(),
            _ => IsAuto(settings.TranslationModel) ? AutoTranslationId : settings.TranslationModel.Trim()
        };
    }

    /// <summary>The engine implied by <paramref name="selectedId"/>.</summary>
    /// <param name="catalog">Decides whether a concrete id is an LLM or a dedicated model.</param>
    public static TranslationEngineKind EngineFor(string? selectedId, ModelCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        if (FakeId.Equals(selectedId, StringComparison.OrdinalIgnoreCase))
        {
            return TranslationEngineKind.Fake;
        }

        if (AutoLlmId.Equals(selectedId, StringComparison.OrdinalIgnoreCase))
        {
            return TranslationEngineKind.LocalLlm;
        }

        // An id the catalog does not recognise falls to the dedicated engine rather than throwing:
        // a settings file naming a model that has since left the catalog must still open.
        return catalog.Find(selectedId)?.Kind == ModelKind.Llm
            ? TranslationEngineKind.LocalLlm
            : TranslationEngineKind.LocalTranslationModel;
    }

    /// <summary>
    /// Writes <paramref name="selectedId"/> back onto <paramref name="settings"/> as the engine plus
    /// the matching model slot.
    ///
    /// <para>The other slot is left alone on purpose. Someone comparing NLLB against an LLM switches
    /// back and forth, and clearing the one they are not using would make them re-pick it every
    /// time.</para>
    /// </summary>
    public static void Apply(AppSettings settings, string? selectedId, ModelCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(catalog);

        var engine = EngineFor(selectedId, catalog);
        settings.TranslationEngine = engine;

        switch (engine)
        {
            case TranslationEngineKind.LocalLlm:
                settings.LlmModel = AutoLlmId.Equals(selectedId, StringComparison.OrdinalIgnoreCase)
                    ? AutoTranslationId
                    : selectedId!.Trim();
                break;

            case TranslationEngineKind.LocalTranslationModel:
                settings.TranslationModel = string.IsNullOrWhiteSpace(selectedId)
                    ? AutoTranslationId
                    : selectedId.Trim();
                break;

            case TranslationEngineKind.Fake:
            default:
                // Fake loads nothing; the model slots keep whatever they held so switching away
                // from the diagnostic engine restores the previous choice.
                break;
        }
    }

    private static bool IsAuto(string? value) =>
        string.IsNullOrWhiteSpace(value) ||
        AutoTranslationId.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase);
}
