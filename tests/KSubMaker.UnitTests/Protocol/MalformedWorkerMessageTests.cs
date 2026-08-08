using System.Text;
using FluentAssertions;
using KSubMaker.WorkerProtocol;
using Xunit;

namespace KSubMaker.UnitTests.Protocol;

/// <summary>
/// Covers "잘못된 Worker 메시지 처리".
///
/// The contract is absolute: <see cref="WorkerProtocolSerializer.DeserializeEvent"/> never throws, and
/// anything it cannot understand becomes an <see cref="UnknownEvent"/> carrying a reason. A stray
/// <c>print</c>, a tqdm bar, a Python warning or a half-flushed line must not be able to take the
/// pipeline down.
/// </summary>
public sealed class MalformedWorkerMessageTests
{
    public static TheoryData<string, string> MalformedLines => new()
    {
        { string.Empty, "empty line" },
        { "   ", "whitespace only" },
        { "\t\t", "tabs only" },
        { "not json", "plain text" },
        { "Warning: torch not compiled with CUDA", "a python warning that escaped to stdout" },
        { " 45%|████      | 45/100 [00:03<00:04]", "a tqdm progress bar" },
        { "{", "a truncated object" },
        { "{\"type\":\"progress\"", "a half-flushed line" },
        { "[1,2,3]", "valid JSON that is an array" },
        { "[]", "an empty JSON array" },
        { "\"just a string\"", "valid JSON that is a bare string" },
        { "123", "valid JSON that is a bare number" },
        { "null", "the JSON null literal" },
        { "{}", "an object with no type" },
        { "{\"stage\":\"transcribing\",\"stageProgress\":10}", "an object with fields but no type" },
        { "{\"type\":null}", "an object whose type is null" },
        { "{\"type\":42}", "an object whose type is not a string" },
        { "{\"type\":\"noSuchEventKind\"}", "an object with an unknown type" },
        { "{\"type\":\"unknown\"}", "the host-only 'unknown' type is not a wire type" },
        { "﻿{\"type\":\"ready\"}", "a byte order mark in front of a valid object" },
        { "{\"type\":\"progress\",\"stage\":}", "a syntax error inside a known event" },
    };

    [Theory]
    [MemberData(nameof(MalformedLines))]
    public void A_malformed_line_becomes_an_UnknownEvent_and_never_throws(string line, string because)
    {
        var act = () => WorkerProtocolSerializer.DeserializeEvent(line);

        act.Should().NotThrow(because);

        var result = WorkerProtocolSerializer.DeserializeEvent(line);

        result.Should().BeOfType<UnknownEvent>(because);
        result.Type.Should().Be("unknown");
    }

    [Theory]
    [MemberData(nameof(MalformedLines))]
    public void An_UnknownEvent_always_explains_itself(string line, string because)
    {
        var unknown = (UnknownEvent)WorkerProtocolSerializer.DeserializeEvent(line);

        unknown.Reason.Should().NotBeNullOrWhiteSpace(because);
    }

    [Fact]
    public void The_raw_line_is_preserved_for_the_log_file()
    {
        var unknown = (UnknownEvent)WorkerProtocolSerializer.DeserializeEvent("  gibberish from the worker  ");

        unknown.Raw.Should().Be("gibberish from the worker");
    }

    [Fact]
    public void A_null_line_is_handled_like_an_empty_one()
    {
        var act = () => WorkerProtocolSerializer.DeserializeEvent(null!);

        act.Should().NotThrow();
        WorkerProtocolSerializer.DeserializeEvent(null!).Should().BeOfType<UnknownEvent>();
    }

    [Fact]
    public void A_one_megabyte_junk_line_is_rejected_without_throwing()
    {
        var junk = new string('x', 1024 * 1024);

        var result = WorkerProtocolSerializer.DeserializeEvent(junk);

        result.Should().BeOfType<UnknownEvent>();
        ((UnknownEvent)result).Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_one_megabyte_line_that_looks_like_json_is_rejected_without_throwing()
    {
        // Starts with '{' so the cheap guard cannot reject it: this really does reach the JSON parser.
        var junk = "{" + new string('x', 1024 * 1024);

        var act = () => WorkerProtocolSerializer.DeserializeEvent(junk);

        act.Should().NotThrow();
        act().Should().BeOfType<UnknownEvent>();
    }

    [Fact]
    public void A_deeply_nested_object_cannot_blow_the_parser_up()
    {
        var builder = new StringBuilder("{\"type\":\"log\",\"level\":\"info\",\"message\":\"x\",\"nested\":");
        const int Depth = 5_000;

        builder.Append('[', Depth).Append(']', Depth).Append('}');

        var act = () => WorkerProtocolSerializer.DeserializeEvent(builder.ToString());

        act.Should().NotThrow();
        act().Should().BeOfType<UnknownEvent>("System.Text.Json refuses to exceed its depth limit");
    }

    /// <summary>
    /// This is the case the hand-rolled discriminator dispatch exists for. <c>[JsonPolymorphic]</c>
    /// requires the discriminator to be the *first* property, and a Python worker building a dict
    /// gives no such guarantee — so a line whose <c>type</c> comes last must still parse.
    /// </summary>
    [Fact]
    public void A_line_whose_discriminator_comes_last_is_still_parsed()
    {
        const string Line = """
            {"stage":"transcribing","stageProgress":42.5,"overallProgress":31.75,"jobId":"job-1","type":"progress"}
            """;

        var result = WorkerProtocolSerializer.DeserializeEvent(Line);

        result.Should().BeOfType<ProgressEvent>();

        var progress = (ProgressEvent)result;
        progress.Stage.Should().Be("transcribing");
        progress.StageProgress.Should().Be(42.5d);
        progress.JobId.Should().Be("job-1");
    }

    [Fact]
    public void A_discriminator_buried_in_the_middle_is_also_found()
    {
        const string Line = """
            {"jobId":"job-1","code":"FFMPEG_FAILED","type":"error","message":"음성 추출 실패","recoverable":true}
            """;

        WorkerProtocolSerializer.DeserializeEvent(Line)
            .Should().BeOfType<ErrorEvent>()
            .Which.Code.Should().Be("FFMPEG_FAILED");
    }

    [Fact]
    public void A_known_event_missing_a_required_field_degrades_to_unknown_instead_of_throwing()
    {
        // ProgressEvent.Stage is `required`: System.Text.Json throws, and the codec must absorb it.
        var act = () => WorkerProtocolSerializer.DeserializeEvent("{\"type\":\"progress\",\"stageProgress\":10}");

        act.Should().NotThrow();
        act().Should().BeOfType<UnknownEvent>();
    }

    [Fact]
    public void A_thousand_malformed_lines_in_a_row_all_come_back_as_unknown()
    {
        var lines = Enumerable.Range(0, 1000)
            .Select(i => (i % 3) switch
            {
                0 => $"garbage {i}",
                1 => "{\"type\":\"nope" + i + "\"}",
                _ => "{" + i
            });

        foreach (var line in lines)
        {
            WorkerProtocolSerializer.DeserializeEvent(line).Should().BeOfType<UnknownEvent>();
        }
    }
}
