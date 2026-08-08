using FluentAssertions;
using KSubMaker.Domain.Models;
using KSubMaker.Domain.Settings;
using Xunit;

namespace KSubMaker.UnitTests.Domain;

/// <summary>
/// The mapping behind the single 번역 모델 dropdown.
///
/// The defect it removes, reported from the app: the model screen finished a download, asked "이
/// 모델로 설정할까요?", set <c>LlmModel</c> — and left <c>TranslationEngine</c> on 전용 번역 모델,
/// so the run kept using NLLB and the answer changed nothing. The settings screen could produce the
/// same dead combination by hand, because engine and model were two independent controls.
///
/// Tested here rather than through the view model because <c>KSubMaker.App</c> is
/// <c>net10.0-windows</c> and the Linux test suite cannot reference it.
/// </summary>
public sealed class TranslationChoiceTests
{
    private static readonly ModelCatalog Catalog = new();

    private static AppSettings Settings(
        TranslationEngineKind engine = TranslationEngineKind.LocalTranslationModel,
        string translation = "auto",
        string llm = "auto") => new()
        {
            TranslationEngine = engine,
            TranslationModel = translation,
            LlmModel = llm
        };

    // -----------------------------------------------------------------------
    // settings -> one id
    // -----------------------------------------------------------------------

    [Fact]
    public void The_dedicated_engine_on_auto_selects_the_auto_entry()
    {
        TranslationChoice.Selected(Settings()).Should().Be(TranslationChoice.AutoTranslationId);
    }

    [Fact]
    public void A_named_nllb_model_selects_itself()
    {
        TranslationChoice.Selected(Settings(translation: ModelIds.TranslationNllb13B))
            .Should().Be(ModelIds.TranslationNllb13B);
    }

    [Fact]
    public void A_named_llm_selects_itself()
    {
        var settings = Settings(TranslationEngineKind.LocalLlm, llm: ModelIds.LlmGemma3_4B);

        TranslationChoice.Selected(settings).Should().Be(ModelIds.LlmGemma3_4B);
    }

    [Fact]
    public void The_llm_engine_on_auto_gets_its_own_entry()
    {
        // The state today's settings screen produces by default. One shared "auto" cannot express
        // both engines, and collapsing it would rewrite an existing configuration on first open.
        var settings = Settings(TranslationEngineKind.LocalLlm, llm: "auto");

        TranslationChoice.Selected(settings).Should().Be(TranslationChoice.AutoLlmId);
    }

    [Fact]
    public void The_fake_engine_selects_the_fake_entry()
    {
        TranslationChoice.Selected(Settings(TranslationEngineKind.Fake))
            .Should().Be(TranslationChoice.FakeId);
    }

    // -----------------------------------------------------------------------
    // one id -> engine
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(ModelIds.TranslationNllb600M, TranslationEngineKind.LocalTranslationModel)]
    [InlineData(ModelIds.TranslationNllb13B, TranslationEngineKind.LocalTranslationModel)]
    [InlineData(ModelIds.LlmGemma3_4B, TranslationEngineKind.LocalLlm)]
    [InlineData(ModelIds.LlmGemma3_12B, TranslationEngineKind.LocalLlm)]
    [InlineData(ModelIds.LlmQwen7B, TranslationEngineKind.LocalLlm)]
    public void The_engine_follows_the_kind_of_the_chosen_model(string id, TranslationEngineKind expected)
    {
        TranslationChoice.EngineFor(id, Catalog).Should().Be(expected);
    }

    [Fact]
    public void An_id_the_catalog_forgot_still_opens_on_the_dedicated_engine()
    {
        // A settings file naming a model that has since left the catalog must not throw the window.
        TranslationChoice.EngineFor("some-retired-model", Catalog)
            .Should().Be(TranslationEngineKind.LocalTranslationModel);
    }

    // -----------------------------------------------------------------------
    // one id -> settings
    // -----------------------------------------------------------------------

    [Fact]
    public void Choosing_an_llm_moves_the_engine_with_it()
    {
        // This is the whole point. Setting the model without the engine is the dead combination.
        var settings = Settings();

        TranslationChoice.Apply(settings, ModelIds.LlmGemma3_4B, Catalog);

        settings.TranslationEngine.Should().Be(TranslationEngineKind.LocalLlm);
        settings.LlmModel.Should().Be(ModelIds.LlmGemma3_4B);
    }

    [Fact]
    public void Choosing_an_nllb_model_moves_the_engine_back()
    {
        var settings = Settings(TranslationEngineKind.LocalLlm, llm: ModelIds.LlmGemma3_4B);

        TranslationChoice.Apply(settings, ModelIds.TranslationNllb13B, Catalog);

        settings.TranslationEngine.Should().Be(TranslationEngineKind.LocalTranslationModel);
        settings.TranslationModel.Should().Be(ModelIds.TranslationNllb13B);
    }

    [Fact]
    public void The_slot_that_was_not_chosen_keeps_its_value()
    {
        // Someone comparing the two engines switches back and forth; clearing the other slot would
        // make them re-pick it every time.
        var settings = Settings(translation: ModelIds.TranslationNllb13B, llm: ModelIds.LlmGemma3_12B);

        TranslationChoice.Apply(settings, ModelIds.LlmGemma3_4B, Catalog);

        settings.TranslationModel.Should().Be(ModelIds.TranslationNllb13B);
    }

    [Fact]
    public void The_fake_entry_selects_the_fake_engine_and_touches_no_model()
    {
        var settings = Settings(translation: ModelIds.TranslationNllb13B, llm: ModelIds.LlmGemma3_4B);

        TranslationChoice.Apply(settings, TranslationChoice.FakeId, Catalog);

        settings.TranslationEngine.Should().Be(TranslationEngineKind.Fake);
        settings.TranslationModel.Should().Be(ModelIds.TranslationNllb13B);
        settings.LlmModel.Should().Be(ModelIds.LlmGemma3_4B);
    }

    [Fact]
    public void The_llm_auto_entry_writes_auto_into_the_llm_slot()
    {
        var settings = Settings();

        TranslationChoice.Apply(settings, TranslationChoice.AutoLlmId, Catalog);

        settings.TranslationEngine.Should().Be(TranslationEngineKind.LocalLlm);
        settings.LlmModel.Should().Be("auto");
    }

    // -----------------------------------------------------------------------
    // round trip
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(TranslationEngineKind.LocalTranslationModel, "auto", "auto")]
    [InlineData(TranslationEngineKind.LocalTranslationModel, ModelIds.TranslationNllb13B, "auto")]
    [InlineData(TranslationEngineKind.LocalLlm, "auto", "auto")]
    [InlineData(TranslationEngineKind.LocalLlm, "auto", ModelIds.LlmGemma3_12B)]
    [InlineData(TranslationEngineKind.Fake, "auto", "auto")]
    public void Opening_and_saving_without_touching_anything_changes_nothing(
        TranslationEngineKind engine, string translation, string llm)
    {
        // What the settings window does when a user opens it and presses 저장. Any drift here
        // rewrites a configuration the user never edited.
        var settings = Settings(engine, translation, llm);

        TranslationChoice.Apply(settings, TranslationChoice.Selected(settings), Catalog);

        settings.TranslationEngine.Should().Be(engine);
        settings.TranslationModel.Should().Be(translation);
        settings.LlmModel.Should().Be(llm);
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        ((Action)(() => TranslationChoice.Selected(null!))).Should().Throw<ArgumentNullException>();
        ((Action)(() => TranslationChoice.EngineFor("x", null!))).Should().Throw<ArgumentNullException>();
        ((Action)(() => TranslationChoice.Apply(null!, "x", Catalog))).Should().Throw<ArgumentNullException>();
    }
}
