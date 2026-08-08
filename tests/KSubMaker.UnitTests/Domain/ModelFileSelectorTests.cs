using FluentAssertions;
using KSubMaker.Domain.Models;
using KSubMaker.Domain.Settings;
using KSubMaker.UnitTests.Fakes;
using Xunit;

namespace KSubMaker.UnitTests.Domain;

/// <summary>
/// The selection rule, replayed against the real hub listings checked in under
/// <c>tests/fixtures/huggingface/</c>. No network: these are captures.
///
/// <para>The bug that produced this file: the catalog hardcoded <c>vocabulary.txt</c> for every
/// Whisper conversion, but <c>faster-whisper-large-v3</c> publishes <c>vocabulary.json</c> plus a
/// <c>preprocessor_config.json</c>. The only feedback channel was a 404 in a user's download
/// dialog.</para>
/// </summary>
public sealed class ModelFileSelectorTests
{
    private const string Qwen7BQuant = @"^qwen2\.5-7b-instruct-q4_k_m(?:-\d+-of-\d+)?\.gguf$";
    private const string Qwen3BQuant = @"^qwen2\.5-3b-instruct-q4_k_m(?:-\d+-of-\d+)?\.gguf$";

    private static ModelDescriptor Descriptor(
        string repositoryId,
        string includePattern,
        string essentialPattern,
        ModelPayloadLayout layout = ModelPayloadLayout.Directory) =>
        new()
        {
            Id = "test",
            Kind = ModelKind.Whisper,
            DisplayName = "테스트 모델",
            RepositoryId = repositoryId,
            IncludePattern = includePattern,
            EssentialFilePattern = essentialPattern,
            Layout = layout,
            FallbackFiles = [],
            ApproxSizeBytes = 0,
            VramGbByComputeType = new Dictionary<ComputeType, double>(),
            License = "MIT",
            Description = "테스트"
        };

    private static ModelDescriptor Ct2(string repositoryId) =>
        Descriptor(repositoryId, ModelFileSelector.AnyFile, ModelFileSelector.Ct2EssentialFile);

    private static ModelDescriptor Gguf(string repositoryId, string includePattern) =>
        Descriptor(
            repositoryId,
            includePattern,
            ModelFileSelector.GgufEssentialFile,
            ModelPayloadLayout.EntryPointFile);

    // -----------------------------------------------------------------------
    // CTranslate2 repositories: one model, take everything that is not furniture
    // -----------------------------------------------------------------------

    [Fact]
    public void LargeV3_resolves_to_the_files_the_repository_really_has()
    {
        const string Repo = "Systran/faster-whisper-large-v3";

        var selected = ModelFileSelector.Select(Ct2(Repo), HuggingFaceListings.Paths(Repo));

        selected.Should().Equal(
            "config.json",
            "model.bin",
            "preprocessor_config.json",
            "tokenizer.json",
            "vocabulary.json");

        selected.Should().NotContain("vocabulary.txt", "that name 404s on this repository");
    }

    [Fact]
    public void The_older_whisper_conversions_keep_vocabulary_txt()
    {
        const string Repo = "Systran/faster-whisper-base";

        var selected = ModelFileSelector.Select(Ct2(Repo), HuggingFaceListings.Paths(Repo));

        selected.Should().Equal("config.json", "model.bin", "tokenizer.json", "vocabulary.txt");
    }

    [Fact]
    public void Readme_gitattributes_and_licence_files_are_excluded()
    {
        const string Repo = "entai2965/nllb-200-distilled-600M-ctranslate2";

        var paths = HuggingFaceListings.Paths(Repo);
        var selected = ModelFileSelector.Select(Ct2(Repo), paths);

        paths.Should().Contain([".gitattributes", "README.md", "LICENSE.model.md"]);
        selected.Should().Equal(
            "config.json",
            "model.bin",
            "sentencepiece.bpe.model",
            "shared_vocabulary.json",
            "special_tokens_map.json",
            "tokenizer.json",
            "tokenizer_config.json");
    }

    [Theory]
    [InlineData(".gitattributes")]
    [InlineData(".gitignore")]
    [InlineData("README.md")]
    [InlineData("readme.MD")]
    [InlineData("LICENSE")]
    [InlineData("LICENSE.txt")]
    [InlineData("LICENCE")]
    [InlineData("LICENSE.model.md")]
    [InlineData("docs/USAGE.md")]
    public void Repository_furniture_never_survives_selection(string path)
    {
        var selected = ModelFileSelector.Select(Ct2("acme/model"), [path, "model.bin"]);

        selected.Should().Equal("model.bin");
    }

    // -----------------------------------------------------------------------
    // GGUF repositories: one quantisation out of many, shards included
    // -----------------------------------------------------------------------

    [Fact]
    public void A_split_quantisation_resolves_to_every_shard_in_order()
    {
        const string Repo = "Qwen/Qwen2.5-7B-Instruct-GGUF";
        var descriptor = Gguf(Repo, Qwen7BQuant);

        var selected = ModelFileSelector.Select(descriptor, HuggingFaceListings.Paths(Repo));

        selected.Should().Equal(
            "qwen2.5-7b-instruct-q4_k_m-00001-of-00002.gguf",
            "qwen2.5-7b-instruct-q4_k_m-00002-of-00002.gguf");

        // The unsplit name the catalog used to ask for has never existed in this repository.
        HuggingFaceListings.Paths(Repo).Should().NotContain("qwen2.5-7b-instruct-q4_k_m.gguf");
    }

    [Fact]
    public void An_unsplit_quantisation_resolves_to_exactly_one_file()
    {
        const string Repo = "Qwen/Qwen2.5-3B-Instruct-GGUF";

        var selected = ModelFileSelector.Select(Gguf(Repo, Qwen3BQuant), HuggingFaceListings.Paths(Repo));

        selected.Should().Equal("qwen2.5-3b-instruct-q4_k_m.gguf");
    }

    [Fact]
    public void The_quant_pattern_does_not_pick_up_neighbouring_quantisations()
    {
        const string Repo = "Qwen/Qwen2.5-7B-Instruct-GGUF";

        var selected = ModelFileSelector.Select(Gguf(Repo, Qwen7BQuant), HuggingFaceListings.Paths(Repo));

        selected.Should().OnlyContain(file => file.Contains("q4_k_m", StringComparison.Ordinal));
        HuggingFaceListings.Paths(Repo).Should().HaveCountGreaterThan(selected.Count + 5,
            "the repository publishes many other quantisations we must not download");
    }

    [Fact]
    public void The_entry_point_of_a_split_model_is_shard_one_regardless_of_input_order()
    {
        const string Repo = "Qwen/Qwen2.5-7B-Instruct-GGUF";
        var descriptor = Gguf(Repo, Qwen7BQuant);

        var reversed = HuggingFaceListings.Paths(Repo).Reverse().ToList();
        var selected = ModelFileSelector.Select(descriptor, reversed);

        // llama.cpp opens the remaining shards itself; shard two on its own is "invalid magic".
        ModelFileSelector.EntryPointFile(descriptor, selected)
            .Should().Be("qwen2.5-7b-instruct-q4_k_m-00001-of-00002.gguf");
    }

    [Fact]
    public void The_entry_point_of_an_unsplit_model_is_the_file_itself()
    {
        const string Repo = "Qwen/Qwen2.5-3B-Instruct-GGUF";
        var descriptor = Gguf(Repo, Qwen3BQuant);

        var selected = ModelFileSelector.Select(descriptor, HuggingFaceListings.Paths(Repo));

        ModelFileSelector.EntryPointFile(descriptor, selected).Should().Be("qwen2.5-3b-instruct-q4_k_m.gguf");
    }

    // -----------------------------------------------------------------------
    // Refusals
    // -----------------------------------------------------------------------

    [Fact]
    public void An_empty_result_is_a_problem_that_names_the_repository()
    {
        var descriptor = Ct2("acme/renamed");

        var selected = ModelFileSelector.Select(descriptor, ["README.md", ".gitattributes"]);

        selected.Should().BeEmpty();
        ModelFileSelector.DescribeProblem(descriptor, selected)
            .Should().NotBeNull().And.Contain("acme/renamed");
    }

    [Fact]
    public void A_result_without_weights_is_a_problem_that_names_the_repository()
    {
        var descriptor = Ct2("acme/tokenizer-only");

        var selected = ModelFileSelector.Select(descriptor, ["tokenizer.json", "config.json"]);

        selected.Should().NotBeEmpty();
        ModelFileSelector.DescribeProblem(descriptor, selected)
            .Should().NotBeNull().And.Contain("acme/tokenizer-only");
    }

    [Fact]
    public void A_healthy_result_has_no_problem()
    {
        var descriptor = Ct2("acme/fine");

        ModelFileSelector.DescribeProblem(descriptor, ["config.json", "model.bin"]).Should().BeNull();
    }

    [Fact]
    public void Duplicate_and_blank_paths_are_ignored()
    {
        var selected = ModelFileSelector.Select(
            Ct2("acme/model"),
            ["model.bin", "model.bin", "   ", "config.json"]);

        selected.Should().Equal("config.json", "model.bin");
    }
}
