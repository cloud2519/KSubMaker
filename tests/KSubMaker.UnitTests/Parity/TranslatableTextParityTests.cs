using System.Text.Json;
using FluentAssertions;
using KSubMaker.Domain.Subtitles;
using Xunit;

namespace KSubMaker.UnitTests.Parity;

/// <summary>
/// The C# and Python pipelines have to agree, cue for cue, on two questions:
/// "is there anything here to translate?" and "is this response broken or merely incomplete?".
///
/// <para>The cases live in <c>tests/fixtures/translation/untranslatable-segments.json</c>, which
/// neither language owns; <c>worker/tests/test_translatable_parity.py</c> replays exactly the same
/// file through <c>batching.has_translatable_content</c> and <c>batching.is_mostly_untranslated</c>.
/// Same precedent as <see cref="ErrorCodeParityTests"/>: a contract spanning two languages needs one
/// artefact both of them are checked against.</para>
/// </summary>
public sealed class TranslatableTextParityTests
{
    private sealed record TextCase(string Text, bool Expected, string Why);

    private sealed record ThresholdCase(int Unusable, int Requested, bool Expected, string Why);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// The build copies the fixture next to the test assembly; the repository walk is a fallback for
    /// a runner that ignores content items.
    /// </summary>
    private static string LocateFixture()
    {
        var copied = Path.Combine(AppContext.BaseDirectory, "TranslationFixtures", "untranslatable-segments.json");
        if (File.Exists(copied))
        {
            return copied;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        for (var depth = 0; directory is not null && depth < 12; depth++, directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName, "tests", "fixtures", "translation", "untranslatable-segments.json");

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return copied;
    }

    private static JsonElement Root()
    {
        var path = LocateFixture();

        File.Exists(path).Should().BeTrue($"the shared parity fixture must be readable (looked at {path})");

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    public static TheoryData<string, bool, string> TextCases()
    {
        var data = new TheoryData<string, bool, string>();

        foreach (var element in Root().GetProperty("translatable").EnumerateArray())
        {
            var item = element.Deserialize<TextCase>(Options)!;
            data.Add(item.Text, item.Expected, item.Why);
        }

        return data;
    }

    public static TheoryData<int, int, bool, string> ThresholdCases()
    {
        var data = new TheoryData<int, int, bool, string>();

        foreach (var element in Root().GetProperty("mostlyUntranslated").EnumerateArray())
        {
            var item = element.Deserialize<ThresholdCase>(Options)!;
            data.Add(item.Unusable, item.Requested, item.Expected, item.Why);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(TextCases))]
    public void The_shared_untranslatable_cases_get_the_same_answer_here_as_in_python(
        string text,
        bool expected,
        string why)
    {
        TranslatableText.HasTranslatableContent(text).Should().Be(expected, why);
    }

    [Theory]
    [MemberData(nameof(ThresholdCases))]
    public void The_shared_mostly_untranslated_cases_get_the_same_answer_here_as_in_python(
        int unusable,
        int requested,
        bool expected,
        string why)
    {
        TranslationValidator.IsMostlyUntranslated(unusable, requested).Should().Be(expected, why);
    }

    [Fact]
    public void The_fixture_actually_contains_cases_of_both_kinds()
    {
        // A fixture that silently stopped loading would make every theory above vacuously green.
        var root = Root();

        root.GetProperty("translatable").GetArrayLength().Should().BeGreaterThan(20);
        root.GetProperty("mostlyUntranslated").GetArrayLength().Should().BeGreaterThan(6);

        TextCases().Should().NotBeEmpty();
        ThresholdCases().Should().NotBeEmpty();
    }

    /// <summary>
    /// The thresholds are two numbers that have to be identical in two files. Pinning them here
    /// means a change on one side shows up as a failing parity test rather than as a subtitle file
    /// that fails on Windows and degrades in the tests.
    /// </summary>
    [Fact]
    public void The_threshold_constants_are_the_documented_values()
    {
        TranslationValidator.MostlyUntranslatedRatio.Should().Be(0.5d);
        TranslationValidator.MostlyUntranslatedMinimumCues.Should().Be(4);
    }
}
