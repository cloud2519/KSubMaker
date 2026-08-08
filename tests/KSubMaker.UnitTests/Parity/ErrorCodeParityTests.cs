using System.Text.RegularExpressions;
using FluentAssertions;
using KSubMaker.Domain.Errors;
using Xunit;

namespace KSubMaker.UnitTests.Parity;

/// <summary>
/// The test <c>ErrorCodes</c> promises in its own doc comment: the C# list and
/// <c>worker/ksubmaker_worker/errors.py</c> must define exactly the same set of code strings.
///
/// The Python file is parsed with a regex rather than executed, so the assertion holds on a machine
/// with no Python installed at all.
/// </summary>
public sealed partial class ErrorCodeParityTests
{
    /// <summary>Matches <c>NAME: Final = "VALUE"</c> at module scope.</summary>
    [GeneratedRegex(
        """^(?<name>[A-Z][A-Z0-9_]*)\s*:\s*Final\s*=\s*"(?<value>[A-Z0-9_]+)"\s*$""",
        RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ConstantPattern();

    /// <summary>Matches the bare code strings listed inside the <c>ALL</c> tuple.</summary>
    [GeneratedRegex(
        """ALL\s*:\s*Final\[tuple\[str,\s*\.\.\.\]\]\s*=\s*\((?<body>[^)]*)\)""",
        RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex AllTuplePattern();

    private static string ReadPythonSource()
    {
        var path = LocateErrorsPy();

        File.Exists(path).Should().BeTrue(
            $"the Python mirror of ErrorCodes must be readable by this test (looked at {path})");

        return File.ReadAllText(path);
    }

    /// <summary>
    /// The build copies the file next to the test assembly; the repository walk is a fallback for a
    /// runner that ignores content items.
    /// </summary>
    private static string LocateErrorsPy()
    {
        var copied = Path.Combine(AppContext.BaseDirectory, "WorkerSource", "errors.py");
        if (File.Exists(copied))
        {
            return copied;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        for (var depth = 0; directory is not null && depth < 12; depth++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "worker", "ksubmaker_worker", "errors.py");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return copied;
    }

    private static string[] PythonConstantValues() =>
        ConstantPattern()
            .Matches(ReadPythonSource())
            .Select(m => m.Groups["value"].Value)
            .ToArray();

    [Fact]
    public void The_python_worker_defines_exactly_the_same_error_codes_as_the_host()
    {
        var python = PythonConstantValues();

        python.Should().NotBeEmpty("the regex must actually have matched something");
        python.Should().BeEquivalentTo(ErrorCodes.All,
            "ErrorCodes.cs and worker/ksubmaker_worker/errors.py are two halves of one contract");
    }

    [Fact]
    public void Neither_side_declares_a_duplicate_code()
    {
        PythonConstantValues().Should().OnlyHaveUniqueItems();
        ErrorCodes.All.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void The_python_constant_names_are_the_snake_cased_form_of_their_values()
    {
        foreach (Match match in ConstantPattern().Matches(ReadPythonSource()))
        {
            match.Groups["name"].Value.Should().Be(match.Groups["value"].Value);
        }
    }

    [Fact]
    public void The_python_ALL_tuple_lists_every_declared_constant_in_the_same_order()
    {
        var source = ReadPythonSource();

        var tuple = AllTuplePattern().Match(source);
        tuple.Success.Should().BeTrue("errors.py must expose an ALL tuple");

        var listed = tuple.Groups["body"].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(entry => entry.Length > 0 && !entry.StartsWith('#'))
            .ToArray();

        listed.Should().Equal(PythonConstantValues());
        listed.Should().Equal(ErrorCodes.All);
    }

    [Fact]
    public void Every_host_code_has_a_korean_user_facing_message()
    {
        foreach (var code in ErrorCodes.All)
        {
            var message = UserFacingErrors.Describe(code);

            message.Should().NotBeNullOrWhiteSpace(code);
            message.Should().Contain("니다", $"{code} must have a Korean sentence, not an English one");
        }
    }

    /// <summary>
    /// The message exists to stop the user from doing the one thing that will not help. A CUDA
    /// support-library failure looks like a driver problem and is not one.
    /// </summary>
    [Fact]
    public void The_cuda_library_message_names_the_libraries_and_the_remedy()
    {
        var message = UserFacingErrors.Describe(ErrorCodes.CudaLibraryMissing);

        message.Should().Contain("cuBLAS");
        message.Should().Contain("cuDNN");
        message.Should().Contain("build-worker.ps1");
        message.Should().NotContain("드라이버를 업데이트");
    }

    [Fact]
    public void An_unknown_code_falls_back_to_the_generic_korean_sentence()
    {
        UserFacingErrors.Describe("SOMETHING_NEW").Should().Be("알 수 없는 오류가 발생했습니다.");
        UserFacingErrors.Describe(null).Should().Be("알 수 없는 오류가 발생했습니다.");
    }

    [Fact]
    public void A_detail_is_appended_in_parentheses_when_supplied()
    {
        UserFacingErrors.Describe(ErrorCodes.FfmpegFailed, "exit 1")
            .Should().EndWith(" (exit 1)");
    }

    [Theory]
    [InlineData(ErrorCodes.CudaOutOfMemory, true)]
    [InlineData(ErrorCodes.WorkerCrashed, true)]
    [InlineData(ErrorCodes.FfmpegFailed, true)]
    [InlineData(ErrorCodes.InvalidTranslationResponse, true)]
    [InlineData(ErrorCodes.VideoNotFound, false)]
    // Not retryable on purpose: the retry loads the same missing DLL from the same directory and
    // fails identically, one whole model load later.
    [InlineData(ErrorCodes.CudaLibraryMissing, false)]
    [InlineData(ErrorCodes.AudioTrackNotFound, false)]
    [InlineData(ErrorCodes.OperationCancelled, false)]
    [InlineData(ErrorCodes.Unknown, false)]
    [InlineData(null, false)]
    [InlineData("NOT_A_REAL_CODE", false)]
    public void IsAutoRetryable_matches_the_documented_recoverable_set(string? code, bool expected)
    {
        ErrorCodes.IsAutoRetryable(code).Should().Be(expected);
    }

    [Fact]
    public void The_python_recoverable_set_matches_the_host_retry_policy()
    {
        var source = ReadPythonSource();

        var start = source.IndexOf("RECOVERABLE", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0);

        var open = source.IndexOf('{', start);
        var close = source.IndexOf('}', open);
        open.Should().BeGreaterThan(0);
        close.Should().BeGreaterThan(open);

        var listed = source[(open + 1)..close]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(entry => entry.Length > 0)
            .ToArray();

        var hostRecoverable = ErrorCodes.All.Where(ErrorCodes.IsAutoRetryable).ToArray();

        listed.Should().BeEquivalentTo(hostRecoverable);
    }
}
