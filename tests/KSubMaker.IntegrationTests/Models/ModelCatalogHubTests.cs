using FluentAssertions;
using KSubMaker.Domain.Models;
using KSubMaker.IntegrationTests.Infrastructure;
using Xunit;

namespace KSubMaker.IntegrationTests.Models;

/// <summary>
/// Asks Hugging Face whether the catalog is still true.
///
/// <para><b>Why this exists.</b> Every earlier failure in this area — <c>vocabulary.txt</c> on
/// large-v3, the <c>600B</c> typo in the default translation repository, a Qwen quantisation that is
/// really two shards — had the same shape: the code was confident and wrong, and the only feedback
/// channel was a user pressing "다운로드". One HTTP call per repository closes that loop.</para>
///
/// <para>The suite is offline by design (ADR-020), so these tests <b>skip</b> when huggingface.co
/// cannot be reached, and <c>ModelCatalogFixtureTests</c> keeps checking the same things against
/// checked-in captures on a machine with no network. Set
/// <c>KSUBMAKER_SKIP_NETWORK_TESTS</c> to keep them out of a run that has connectivity.</para>
/// </summary>
public sealed class ModelCatalogHubTests(HuggingFaceTreeCache cache) : IClassFixture<HuggingFaceTreeCache>
{
    private readonly HuggingFaceTreeCache _cache = cache;

    /// <summary>The listing can legitimately grow; a size that is wildly off cannot be a new README.</summary>
    private const double SizeTolerance = 0.02d;

    public static TheoryData<string> BuiltInModelIds()
    {
        var data = new TheoryData<string>();

        foreach (var descriptor in ModelCatalog.BuiltIn())
        {
            data.Add(descriptor.Id);
        }

        return data;
    }

    private static ModelDescriptor Get(string modelId) =>
        ModelCatalog.BuiltIn().Single(descriptor => descriptor.Id == modelId);

    [RequiresNetworkTheory]
    [MemberData(nameof(BuiltInModelIds))]
    public async Task The_repository_exists(string modelId)
    {
        var descriptor = Get(modelId);

        var listing = await _cache.GetAsync(descriptor.RepositoryId);

        listing.Ok.Should().BeTrue(
            "{0}의 저장소 {1}이(가) HTTP {2}를 반환했습니다",
            descriptor.Id, descriptor.RepositoryId, (int)listing.StatusCode);
    }

    [RequiresNetworkTheory]
    [MemberData(nameof(BuiltInModelIds))]
    public async Task The_selection_rule_resolves_to_a_usable_set(string modelId)
    {
        var descriptor = Get(modelId);
        var listing = await _cache.GetAsync(descriptor.RepositoryId);
        listing.Ok.Should().BeTrue("the repository has to answer before its contents can be judged");

        var selected = ModelFileSelector.Select(descriptor, listing.Paths);

        selected.Should().NotBeEmpty(
            "{0}의 파일 선택 규칙이 {1}에서 아무 파일도 고르지 못했습니다", descriptor.Id, descriptor.RepositoryId);

        ModelFileSelector.DescribeProblem(descriptor, selected).Should().BeNull();
    }

    [RequiresNetworkTheory]
    [MemberData(nameof(BuiltInModelIds))]
    public async Task Every_offline_fallback_file_exists_in_the_repository(string modelId)
    {
        var descriptor = Get(modelId);
        var listing = await _cache.GetAsync(descriptor.RepositoryId);
        listing.Ok.Should().BeTrue();

        // This is the assertion that would have caught the original bug: the fallback list is what
        // ships, and "vocabulary.txt" was simply not in this repository.
        descriptor.FallbackFiles.Should().BeSubsetOf(listing.Paths);
    }

    [RequiresNetworkTheory]
    [MemberData(nameof(BuiltInModelIds))]
    public async Task The_essential_file_is_present(string modelId)
    {
        var descriptor = Get(modelId);
        var listing = await _cache.GetAsync(descriptor.RepositoryId);
        listing.Ok.Should().BeTrue();

        var selected = ModelFileSelector.Select(descriptor, listing.Paths);

        var entryPoint = ModelFileSelector.EntryPointFile(descriptor, selected);
        entryPoint.Should().NotBeNullOrWhiteSpace();

        if (descriptor.Layout == ModelPayloadLayout.EntryPointFile)
        {
            // Handing llama.cpp anything but shard one fails with "invalid magic".
            entryPoint.Should().Be(selected[0]);
        }
    }

    [RequiresNetworkTheory]
    [MemberData(nameof(BuiltInModelIds))]
    public async Task The_estimated_size_still_matches_the_repository(string modelId)
    {
        var descriptor = Get(modelId);
        var listing = await _cache.GetAsync(descriptor.RepositoryId);
        listing.Ok.Should().BeTrue();

        var selected = ModelFileSelector.Select(descriptor, listing.Paths);
        var measured = listing.TotalBytes(selected);

        measured.Should().BeGreaterThan(0);
        descriptor.ApproxSizeBytes.Should().BeCloseTo(measured, (ulong)(measured * SizeTolerance),
            "{0}의 예상 디스크 용량이 실제 저장소 크기와 어긋났습니다", descriptor.Id);
    }

    [RequiresNetworkFact]
    public async Task The_repository_that_does_not_exist_is_reported_as_missing()
    {
        // The typo the catalog shipped with. Kept as a test so the failure mode stays legible: an
        // id that looks plausible answers with an error status, not with an empty file list.
        var listing = await _cache.GetAsync("entai2965/nllb-200-distilled-600B-ctranslate2");

        listing.Ok.Should().BeFalse("'600B' is a typo for '600M' and no such repository exists");
    }
}
