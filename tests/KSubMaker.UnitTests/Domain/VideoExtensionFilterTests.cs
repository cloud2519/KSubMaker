using FluentAssertions;
using KSubMaker.Domain.Media;
using Xunit;

namespace KSubMaker.UnitTests.Domain;

/// <summary>Covers the "확장자 필터" requirement: all ten extensions, case-insensitively.</summary>
public sealed class VideoExtensionFilterTests
{
    public static TheoryData<string> AllVideoExtensions =>
        new(".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".m4v", ".ts", ".mts", ".m2ts");

    [Fact]
    public void Default_contains_exactly_the_ten_specified_extensions()
    {
        VideoExtensions.Default.Should().BeEquivalentTo(
            [".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".m4v", ".ts", ".mts", ".m2ts"]);
    }

    [Theory]
    [MemberData(nameof(AllVideoExtensions))]
    public void Every_supported_extension_is_accepted_in_lower_case(string extension)
    {
        VideoExtensions.IsVideo("/videos/movie" + extension).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(AllVideoExtensions))]
    public void Every_supported_extension_is_accepted_in_upper_case(string extension)
    {
        VideoExtensions.IsVideo("/videos/MOVIE" + extension.ToUpperInvariant()).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(AllVideoExtensions))]
    public void Every_supported_extension_is_accepted_in_mixed_case(string extension)
    {
        var mixed = string.Concat(extension.Select((c, i) => i % 2 == 0 ? char.ToUpperInvariant(c) : c));
        VideoExtensions.IsVideo("/videos/movie" + mixed).Should().BeTrue();
    }

    [Theory]
    [InlineData("/videos/readme.txt")]
    [InlineData("/videos/poster.jpg")]
    [InlineData("/videos/subtitle.srt")]
    [InlineData("/videos/archive.mp4.zip")]
    [InlineData("/videos/movie.mp")]
    [InlineData("/videos/movie.mp42")]
    public void Non_video_extensions_are_rejected(string path)
    {
        VideoExtensions.IsVideo(path).Should().BeFalse();
    }

    [Theory]
    [InlineData("/videos/no-extension")]
    [InlineData("noextension")]
    public void Files_without_an_extension_are_rejected(string path)
    {
        VideoExtensions.IsVideo(path).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_paths_are_rejected(string path)
    {
        VideoExtensions.IsVideo(path).Should().BeFalse();
    }

    [Theory]
    [InlineData("/videos/.mp4", true)]                      // dotfile whose whole name is the extension
    [InlineData("/videos/영화 (2026) [1080p].MKV", true)]     // Korean, spaces, brackets
    [InlineData("/videos/movie.part1.mp4", true)]           // multiple dots
    [InlineData("/videos/movie.mp4.", false)]               // trailing dot: no extension
    [InlineData("/videos/tricky.mkv.txt", false)]
    [InlineData("/videos/..", false)]
    public void Weird_names_are_classified_by_the_final_extension_only(string path, bool expected)
    {
        VideoExtensions.IsVideo(path).Should().Be(expected);
    }

    [Fact]
    public void A_custom_extension_set_overrides_the_default()
    {
        var onlyMkv = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mkv" };

        VideoExtensions.IsVideo("/videos/movie.MKV", onlyMkv).Should().BeTrue();
        VideoExtensions.IsVideo("/videos/movie.mp4", onlyMkv).Should().BeFalse();
    }

    [Fact]
    public void Subtitle_extension_set_is_case_insensitive()
    {
        VideoExtensions.Subtitle.Should().Contain(".srt");
        VideoExtensions.Subtitle.Contains(".SRT").Should().BeTrue();
        VideoExtensions.Subtitle.Contains(".mp4").Should().BeFalse();
    }
}
