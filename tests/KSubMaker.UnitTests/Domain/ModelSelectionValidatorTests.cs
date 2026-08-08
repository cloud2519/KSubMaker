using FluentAssertions;
using KSubMaker.Domain.Hardware;
using KSubMaker.Domain.Models;
using KSubMaker.Domain.Settings;
using Xunit;

namespace KSubMaker.UnitTests.Domain;

/// <summary>
/// The rules behind the settings screen's 설치됨 / 미설치 labels and its save-time warning.
///
/// The failure they exist to prevent, from a real log: the user picked <c>whisper-small</c> in the
/// settings screen, nothing said it had never been downloaded, and the job died with
/// <c>WHISPER_MODEL_NOT_FOUND: whisper-small</c> after the queue had already started.
///
/// Tested here rather than through the view model because <c>KSubMaker.App</c> is
/// <c>net10.0-windows</c> and the Linux test suite cannot reference it at all.
/// </summary>
public sealed class ModelSelectionValidatorTests
{
    private static readonly ModelCatalog Catalog = new();

    private static AppSettings Settings(
        string whisper = "auto",
        string translation = "auto",
        string llm = "auto",
        TranslationEngineKind engine = TranslationEngineKind.LocalTranslationModel,
        bool fakeAi = false) => new()
        {
            WhisperModel = whisper,
            TranslationModel = translation,
            LlmModel = llm,
            TranslationEngine = engine,
            FakeAiMode = fakeAi
        };

    // -----------------------------------------------------------------------
    // the happy paths
    // -----------------------------------------------------------------------

    [Fact]
    public void Nothing_is_reported_when_every_selected_model_is_installed()
    {
        var settings = Settings(whisper: ModelIds.WhisperSmall, translation: ModelIds.TranslationNllb600M);

        var issues = ModelSelectionValidator.FindMissing(
            settings, Catalog, [ModelIds.WhisperSmall, ModelIds.TranslationNllb600M]);

        issues.Should().BeEmpty();
        ModelSelectionValidator.Describe(issues).Should().BeEmpty();
    }

    [Fact]
    public void Auto_is_never_an_issue_even_with_nothing_installed()
    {
        // In the settings screen "auto" means "I have not chosen", and warning about a choice the
        // user did not make would be noise. Resolve/FindMissingToRun is the counterpart that does
        // resolve it, and it runs at 시작 where something concrete is about to be loaded.
        ModelSelectionValidator.FindMissing(Settings(), Catalog, []).Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("AUTO")]
    public void A_blank_or_differently_cased_auto_is_also_ignored(string value)
    {
        ModelSelectionValidator.FindMissing(Settings(whisper: value), Catalog, []).Should().BeEmpty();
    }

    [Fact]
    public void Installed_ids_are_matched_case_insensitively()
    {
        var settings = Settings(whisper: ModelIds.WhisperLargeV3Turbo);

        ModelSelectionValidator.FindMissing(settings, Catalog, ["WHISPER-LARGE-V3-TURBO"])
            .Should().BeEmpty();
    }

    // -----------------------------------------------------------------------
    // what gets reported
    // -----------------------------------------------------------------------

    [Fact]
    public void A_whisper_model_that_was_never_downloaded_is_reported()
    {
        var settings = Settings(whisper: ModelIds.WhisperSmall);

        var issue = ModelSelectionValidator
            .FindMissing(settings, Catalog, [ModelIds.TranslationNllb600M])
            .Should().ContainSingle().Subject;

        issue.Kind.Should().Be(ModelKind.Whisper);
        issue.ModelId.Should().Be(ModelIds.WhisperSmall);
        issue.DisplayName.Should().Be(Catalog.Get(ModelIds.WhisperSmall).DisplayName);
    }

    [Fact]
    public void Both_slots_are_reported_in_settings_screen_order()
    {
        var settings = Settings(whisper: ModelIds.WhisperMedium, translation: ModelIds.TranslationNllb13B);

        var issues = ModelSelectionValidator.FindMissing(settings, Catalog, []);

        issues.Select(i => i.Kind).Should().Equal(ModelKind.Whisper, ModelKind.Translation);
    }

    [Fact]
    public void An_id_the_catalog_has_never_heard_of_is_reported_under_its_own_name()
    {
        var settings = Settings(whisper: "whisper-imaginary-v9");

        var issue = ModelSelectionValidator.FindMissing(settings, Catalog, []).Should().ContainSingle().Subject;

        issue.ModelId.Should().Be("whisper-imaginary-v9");
        issue.DisplayName.Should().Be("whisper-imaginary-v9", "there is no catalog entry to name it");
    }

    [Fact]
    public void Surrounding_whitespace_in_a_persisted_id_is_tolerated()
    {
        var settings = Settings(whisper: $"  {ModelIds.WhisperSmall}  ");

        ModelSelectionValidator.FindMissing(settings, Catalog, [ModelIds.WhisperSmall]).Should().BeEmpty();
    }

    // -----------------------------------------------------------------------
    // only the engine that will actually run is checked
    // -----------------------------------------------------------------------

    [Fact]
    public void The_llm_model_is_ignored_while_the_dedicated_engine_is_selected()
    {
        var settings = Settings(
            whisper: ModelIds.WhisperSmall,
            translation: ModelIds.TranslationNllb600M,
            llm: ModelIds.LlmQwen7B,
            engine: TranslationEngineKind.LocalTranslationModel);

        ModelSelectionValidator
            .FindMissing(settings, Catalog, [ModelIds.WhisperSmall, ModelIds.TranslationNllb600M])
            .Should().BeEmpty("an NLLB user will never load the LLM");
    }

    [Fact]
    public void The_translation_model_is_ignored_while_the_llm_engine_is_selected()
    {
        var settings = Settings(
            whisper: ModelIds.WhisperSmall,
            translation: ModelIds.TranslationNllb13B,
            llm: ModelIds.LlmQwen3B,
            engine: TranslationEngineKind.LocalLlm);

        var issue = ModelSelectionValidator
            .FindMissing(settings, Catalog, [ModelIds.WhisperSmall])
            .Should().ContainSingle().Subject;

        issue.Kind.Should().Be(ModelKind.Llm);
        issue.ModelId.Should().Be(ModelIds.LlmQwen3B);
    }

    [Fact]
    public void The_fake_engine_needs_no_translation_model_at_all()
    {
        var settings = Settings(
            whisper: ModelIds.WhisperSmall,
            translation: ModelIds.TranslationNllb13B,
            engine: TranslationEngineKind.Fake);

        ModelSelectionValidator.FindMissing(settings, Catalog, [ModelIds.WhisperSmall]).Should().BeEmpty();
    }

    [Fact]
    public void Fake_ai_mode_suppresses_every_warning()
    {
        // It is the mode people switch to precisely because nothing is downloaded yet.
        var settings = Settings(whisper: ModelIds.WhisperLargeV3, translation: ModelIds.TranslationNllb13B, fakeAi: true);

        ModelSelectionValidator.FindMissing(settings, Catalog, []).Should().BeEmpty();
    }

    // -----------------------------------------------------------------------
    // the sentence the user reads
    // -----------------------------------------------------------------------

    [Fact]
    public void The_warning_names_every_model_and_points_at_the_models_screen()
    {
        var settings = Settings(whisper: ModelIds.WhisperSmall, translation: ModelIds.TranslationNllb600M);

        var message = ModelSelectionValidator.Describe(ModelSelectionValidator.FindMissing(settings, Catalog, []));

        message.Should().Contain(ModelIds.WhisperSmall);
        message.Should().Contain(ModelIds.TranslationNllb600M);
        message.Should().Contain("모델 관리");
        message.Should().Contain("음성 인식 모델");
        message.Should().Contain("번역 모델");
        message.Should().Contain("니다", "the settings screen is Korean");
    }

    [Theory]
    [InlineData(ModelKind.Whisper, "음성 인식 모델")]
    [InlineData(ModelKind.Translation, "번역 모델")]
    [InlineData(ModelKind.Llm, "로컬 LLM 모델")]
    public void Every_kind_has_a_korean_name(ModelKind kind, string expected)
    {
        ModelSelectionValidator.DescribeKind(kind).Should().Be(expected);
    }

    [Fact]
    public void Null_arguments_are_rejected_rather_than_silently_passing()
    {
        var nullSettings = () => ModelSelectionValidator.FindMissing(null!, Catalog, []);
        var nullCatalog = () => ModelSelectionValidator.FindMissing(Settings(), null!, []);
        var nullInstalled = () => ModelSelectionValidator.FindMissing(Settings(), Catalog, null!);

        nullSettings.Should().Throw<ArgumentNullException>();
        nullCatalog.Should().Throw<ArgumentNullException>();
        nullInstalled.Should().Throw<ArgumentNullException>();
        ((Action)(() => ModelSelectionValidator.Describe(null!))).Should().Throw<ArgumentNullException>();
    }

    // -----------------------------------------------------------------------
    // Resolve / FindMissingToRun — the 시작 pre-flight
    // -----------------------------------------------------------------------
    //
    // The defect these cover: "auto" reached the worker untouched, where it mapped to a hardcoded
    // whisper-small, and the run failed with WHISPER_MODEL_NOT_FOUND minutes in, naming a model the
    // user had never selected. IModelManager.ResolveModelIdAsync was written to prevent exactly that
    // and had no call sites at all.

    private static HardwareRecommendation Recommendation(
        string whisper = ModelIds.WhisperLargeV3,
        string translation = ModelIds.TranslationNllb13B,
        string llm = ModelIds.LlmQwen7B) => new()
        {
            WhisperModelId = whisper,
            TranslationModelId = translation,
            LlmModelId = llm,
            ComputeType = ComputeType.Float16,
            Strategy = ProcessingStrategy.SequentialPerFile,
            BeamSize = 5,
            CanCoResideModels = true,
            UseGpu = true,
            MaxParallelCpuTasks = 1,
            Rationale = string.Empty
        };

    [Fact]
    public void Auto_resolves_to_the_hardware_recommendation()
    {
        var required = ModelSelectionValidator.Resolve(Settings(), Catalog, Recommendation(), []);

        required.Select(r => r.ModelId).Should()
            .Equal(ModelIds.WhisperLargeV3, ModelIds.TranslationNllb13B);
        required.Should().OnlyContain(r => r.FromRecommendation);
        required.Should().OnlyContain(r => !r.IsInstalled);
    }

    [Fact]
    public void An_explicit_choice_beats_the_recommendation()
    {
        var settings = Settings(whisper: ModelIds.WhisperMedium);

        var whisper = ModelSelectionValidator.Resolve(settings, Catalog, Recommendation(), [])
            .Single(r => r.Kind == ModelKind.Whisper);

        whisper.ModelId.Should().Be(ModelIds.WhisperMedium);
        whisper.FromRecommendation.Should().BeFalse();
    }

    [Fact]
    public void Only_the_engine_that_will_run_is_required()
    {
        var llm = ModelSelectionValidator.Resolve(
            Settings(engine: TranslationEngineKind.LocalLlm), Catalog, Recommendation(), []);

        llm.Select(r => r.Kind).Should().Equal(ModelKind.Whisper, ModelKind.Llm);

        var fake = ModelSelectionValidator.Resolve(
            Settings(engine: TranslationEngineKind.Fake), Catalog, Recommendation(), []);

        fake.Select(r => r.Kind).Should().Equal(ModelKind.Whisper);
    }

    [Fact]
    public void Fake_ai_mode_requires_nothing()
    {
        ModelSelectionValidator.Resolve(Settings(fakeAi: true), Catalog, Recommendation(), [])
            .Should().BeEmpty();
    }

    [Fact]
    public void A_slot_with_no_recommendation_to_resolve_it_is_omitted_rather_than_guessed()
    {
        // Inventing a default here is the defect, not the fix: the worker's hardcoded whisper-small
        // is exactly that, and it produced an error naming a model nobody chose.
        ModelSelectionValidator.Resolve(Settings(), Catalog, recommendation: null, [])
            .Should().BeEmpty();
    }

    [Fact]
    public void FindMissingToRun_reports_only_what_is_absent()
    {
        var missing = ModelSelectionValidator.FindMissingToRun(
            Settings(), Catalog, Recommendation(), [ModelIds.WhisperLargeV3]);

        missing.Select(r => r.ModelId).Should().Equal(ModelIds.TranslationNllb13B);
        missing.Single().ApproxSizeBytes.Should().Be(Catalog.Get(ModelIds.TranslationNllb13B).ApproxSizeBytes);
    }

    [Fact]
    public void FindMissingToRun_is_empty_once_everything_is_on_disk()
    {
        ModelSelectionValidator.FindMissingToRun(
            Settings(), Catalog, Recommendation(),
            [ModelIds.WhisperLargeV3, ModelIds.TranslationNllb13B])
            .Should().BeEmpty();
    }
}
