using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace KSubMaker.UnitTests.Packaging;

/// <summary>
/// <c>Strings.Designer.cs</c> says "auto-generated" but is written by hand, because the build agent
/// has no Visual Studio to run the single-file generator (AGENTS.md §8). Nothing except this test
/// notices when someone adds a string to one of the two files and forgets the other — and the
/// failure mode is a screen showing the resource key instead of Korean.
/// </summary>
public sealed partial class StringResourceParityTests
{
    private static string PackagedPath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "AppPackaging", name);

    private static IReadOnlyDictionary<string, string> ResxEntries()
    {
        var path = PackagedPath("Strings.resx.xml");
        File.Exists(path).Should().BeTrue($"the resx must be copied to the output ({path})");

        return XDocument.Load(path).Root!
            .Elements("data")
            .Where(e => e.Attribute("name") is not null)
            .ToDictionary(
                e => e.Attribute("name")!.Value,
                e => e.Element("value")?.Value ?? string.Empty,
                StringComparer.Ordinal);
    }

    private static IReadOnlySet<string> DesignerProperties()
    {
        var path = PackagedPath("Strings.Designer.cs.txt");
        File.Exists(path).Should().BeTrue($"the designer file must be copied to the output ({path})");

        return PropertyPattern()
            .Matches(File.ReadAllText(path))
            .Select(m => m.Groups["name"].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Matches <c>public static string Foo =&gt; Get(nameof(Foo));</c> and nothing else.</summary>
    [GeneratedRegex(@"public static string (?<name>\w+) => Get\(nameof\(\k<name>\)\);")]
    private static partial Regex PropertyPattern();

    [Fact]
    public void Every_resource_key_has_a_property()
    {
        DesignerProperties().Should().BeEquivalentTo(ResxEntries().Keys);
    }

    [Fact]
    public void The_table_is_not_empty()
    {
        ResxEntries().Should().HaveCountGreaterThan(100, "a truncated resx would pass the parity check trivially");
    }

    [Fact]
    public void No_resource_value_is_blank()
    {
        ResxEntries()
            .Where(e => string.IsNullOrWhiteSpace(e.Value))
            .Select(e => e.Key)
            .Should().BeEmpty("a blank value shows as an empty label, which reads as a rendering bug");
    }

    /// <summary>
    /// The strings this task added. Spot-checked by name so that deleting the 고유명사 사전 or
    /// 자막 원본 UI without deleting its strings — or the other way round — is caught here.
    /// </summary>
    [Theory]
    [InlineData("GlossaryGroup")]
    [InlineData("GlossaryAddButton")]
    [InlineData("GlossaryRemoveButton")]
    [InlineData("GlossarySourceRequired")]
    [InlineData("GlossaryTargetRequired")]
    [InlineData("GlossaryDuplicateFormat")]
    [InlineData("ColumnSubtitleSource")]
    [InlineData("SubtitleSourceMenuItem")]
    [InlineData("SubtitleSourceDialogTitle")]
    [InlineData("AskPerFileTitle")]
    [InlineData("AskPerFileMessageFormat")]
    public void The_new_screens_have_their_strings(string key)
    {
        ResxEntries().Should().ContainKey(key);
        DesignerProperties().Should().Contain(key);
    }

    /// <summary>
    /// 선택 항목 제거 and the split of "아무것도 선택하지 않았다" from "선택한 것을 이 동작에 쓸 수 없다".
    /// Deleting one of these keys turns an explanation back into a raw resource name on screen —
    /// which is how the conflated no-selection alert looked to the user who reported it.
    /// </summary>
    [Theory]
    [InlineData("RemoveSelectedButton")]
    [InlineData("RemoveSelectedConfirmTitle")]
    [InlineData("RemoveSelectedConfirmFormat")]
    [InlineData("RemoveSelectedDoneFormat")]
    [InlineData("RemoveSelectedPartialFormat")]
    [InlineData("RemoveSelectedRunningSkipped")]
    [InlineData("SelectionNotCancellableMessage")]
    [InlineData("SelectionNotRetryableMessage")]
    [InlineData("SelectionNotEligibleMessage")]
    public void The_selection_and_removal_messages_exist_in_both_files(string key)
    {
        ResxEntries().Should().ContainKey(key);
        DesignerProperties().Should().Contain(key);
    }

    /// <summary>
    /// The removal prompt has to answer the question the user actually has before they click: the
    /// cache goes, the source video and any subtitle already written do not.
    /// </summary>
    [Fact]
    public void The_removal_prompt_says_what_is_and_is_not_deleted()
    {
        var prompt = ResxEntries()["RemoveSelectedConfirmFormat"];

        prompt.Should().Contain("{0}", "the count of jobs being removed is the point of the prompt");
        prompt.Should().Contain("캐시");
        prompt.Should().Contain("체크포인트");
        prompt.Should().Contain("오디오");
        prompt.Should().Contain("원본 영상");
        prompt.Should().Contain("삭제되지 않습니다");
    }

    /// <summary>
    /// The two refusals must not read alike: the whole defect was one sentence standing in for both.
    /// </summary>
    [Fact]
    public void The_ineligible_messages_do_not_repeat_the_no_selection_sentence()
    {
        var entries = ResxEntries();

        foreach (var key in new[]
                 {
                     "SelectionNotCancellableMessage",
                     "SelectionNotRetryableMessage",
                     "SelectionNotEligibleMessage"
                 })
        {
            entries[key].Should().NotBe(entries["NoSelectionMessage"]);
            entries[key].Any(IsHangul).Should().BeTrue($"{key}는 사용자에게 보이는 문장입니다");
        }

        entries["SelectionNotCancellableMessage"].Should().Contain("취소");
        entries["SelectionNotRetryableMessage"].Should().Contain("다시 시도");
    }

    /// <summary>
    /// The table is Korean, which the rest of the suite already asserts on individual sentences.
    /// A blanket "must contain Hangul" rule is not worth writing here: acronyms (GPU, VRAM) and
    /// placeholder-only formats such as <c>{0} · VRAM {1:0.#}GB</c> are legitimately Latin, and the
    /// allow-list needed to encode that would have to be edited for every new label.
    /// </summary>
    [Fact]
    public void The_glossary_and_subtitle_source_messages_are_korean()
    {
        var entries = ResxEntries();

        foreach (var key in new[] { "GlossarySourceRequired", "GlossaryDuplicateFormat", "SubtitleSourceHint" })
        {
            entries[key].Any(IsHangul).Should().BeTrue($"{key}는 사용자에게 보이는 문장입니다");
        }
    }

    private static bool IsHangul(char c) => c is >= '가' and <= '힣' or >= 'ㄱ' and <= 'ㅎ';
}
