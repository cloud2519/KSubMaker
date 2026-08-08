using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace KSubMaker.UnitTests.Packaging;

/// <summary>
/// Guards against the PowerShell pipeline-unwrapping trap in the shipped scripts.
///
/// Every script runs under <c>Set-StrictMode -Version Latest</c>. On <b>Windows PowerShell 5.1</b> a
/// pipeline that produces nothing assigns <c>$null</c>, and one that produces a single item assigns
/// that item rather than a one-element array. Reading <c>.Count</c> off either is a hard error:
///
/// <code>
///   $missing = @($hardware.missingCudaLibraries) | Where-Object { $_ }   # empty -> $null
///   if ($missing.Count -gt 0) { ... }
///   # 이 개체에서 'Count' 속성을 찾을 수 없습니다.
/// </code>
///
/// The <c>@()</c> above binds only to the property; the pipeline that follows undoes it. The fix is
/// to wrap the <b>whole</b> pipeline: <c>@(@($x) | Where-Object { $_ })</c>.
///
/// This shipped broken once, and it is invisible to every check available on a build agent:
/// PowerShell 7 gives scalars and <c>$null</c> a synthetic <c>.Count</c>, so the script parses,
/// analyses and — on pwsh — runs perfectly. Only Windows PowerShell 5.1 fails, and only on the
/// branch where the collection happens to be empty. Hence a static rule.
/// </summary>
public sealed class PowerShellArrayUnwrapTests
{
    private static string ScriptDirectory => Path.Combine(AppContext.BaseDirectory, "ShippedScripts");

    /// <summary>
    /// An assignment whose right-hand side is a pipeline into a collection-producing cmdlet.
    /// <c>Select-Object -First/-Last</c> is deliberately excluded: assigning a single object from it
    /// is the normal, intended usage (<c>Get-Command ... | Select-Object -First 1</c>).
    /// </summary>
    private static readonly Regex RiskyAssignment = new(
        @"^\s*\$[\w.]+\s*=\s*(?<rhs>.+)$",
        RegexOptions.Compiled);

    private static readonly string[] CollectionCmdlets =
        ["Where-Object", "ForEach-Object", "Group-Object", "Sort-Object"];

    /// <summary>
    /// True when the right-hand side is a bare pipeline into a collection-producing cmdlet.
    ///
    /// The pipe has to be at bracket depth zero. A pipeline nested inside parentheses is almost
    /// always being folded into something scalar — <c>$s = (… | ForEach-Object {…}) -join "`n"</c>
    /// yields a string, which has no <c>.Count</c> hazard at all. Flagging those would be noise, and
    /// a noisy rule gets suppressed rather than obeyed.
    /// </summary>
    private static bool IsBarePipelineIntoCollection(string rhs)
    {
        var depth = 0;
        var inSingle = false;
        var inDouble = false;

        for (var i = 0; i < rhs.Length; i++)
        {
            var c = rhs[i];

            if (inSingle)
            {
                if (c == '\'') { inSingle = false; }
                continue;
            }

            if (inDouble)
            {
                if (c == '"') { inDouble = false; }
                continue;
            }

            switch (c)
            {
                case '\'': inSingle = true; continue;
                case '"': inDouble = true; continue;
                case '(' or '[' or '{': depth++; continue;
                case ')' or ']' or '}': depth--; continue;
                case '|' when depth == 0:
                {
                    var rest = rhs[(i + 1)..].TrimStart();
                    if (CollectionCmdlets.Any(c2 => rest.StartsWith(c2, StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }

                    continue;
                }
            }
        }

        return false;
    }

    public static TheoryData<string> Scripts()
    {
        var data = new TheoryData<string>();

        foreach (var path in Directory.EnumerateFiles(ScriptDirectory, "*.ps1").OrderBy(p => p, StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(path));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Scripts))]
    public void A_pipeline_assigned_to_a_variable_is_wrapped_in_an_array_subexpression(string fileName)
    {
        var lines = File.ReadAllLines(Path.Combine(ScriptDirectory, fileName), Encoding.UTF8);
        var offenders = new List<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            var match = RiskyAssignment.Match(lines[i]);
            if (!match.Success)
            {
                continue;
            }

            var rhs = match.Groups["rhs"].Value.Trim();

            // Gather continuation lines so a pipeline split across several lines is judged whole.
            var j = i;
            while (rhs.EndsWith('|') && j + 1 < lines.Length)
            {
                rhs += ' ' + lines[++j].Trim();
            }

            if (!IsBarePipelineIntoCollection(rhs))
            {
                continue;
            }

            // Wrapping the whole right-hand side in @( ) or a cast makes the result an array in
            // every PowerShell version, which is the only thing that makes .Count safe.
            if (WrapsEntireExpression(rhs))
            {
                continue;
            }

            offenders.Add($"  {fileName}:{i + 1}  {lines[i].Trim()}");
        }

        offenders.Should().BeEmpty(
            "a pipeline result assigned without @( ) collapses to $null (empty) or a bare item " +
            "(single) on Windows PowerShell 5.1, and .Count then throws under Set-StrictMode. " +
            "Wrap the whole right-hand side:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void The_rule_actually_matches_the_shape_that_broke()
    {
        // The exact line that failed on the user's machine, and its fix. Keeping both here means a
        // future loosening of the regex fails loudly instead of silently covering nothing.
        const string Broken = "    $missingCudaLibraries = @($hardware.missingCudaLibraries) | Where-Object { $_ }";
        const string Fixed = "    $missingCudaLibraries = @(@($hardware.missingCudaLibraries) | Where-Object { $_ })";

        IsFlagged(Broken).Should().BeTrue("this is the shape that threw 'Count' 속성을 찾을 수 없습니다");
        IsFlagged(Fixed).Should().BeFalse("wrapping the whole pipeline is the fix");

        // Intended single-object assignments must not be flagged.
        IsFlagged("    $dotnet = Get-Command -Name 'dotnet' -CommandType Application | Select-Object -First 1")
            .Should().BeFalse("Select-Object -First 1 is meant to yield one object");
        IsFlagged("    $tools = Join-Path (Get-KsmRepoRoot) 'tools'")
            .Should().BeFalse("no pipeline at all");
        IsFlagged("    $s = ($items | ForEach-Object { \"- $_\" }) -join \"`n\"")
            .Should().BeFalse("a pipeline folded into a string by -join has no .Count hazard");
    }

    /// <summary>
    /// True when an <c>@( … )</c> (or a cast) encloses the <b>entire</b> expression.
    ///
    /// Merely starting with <c>@(</c> proves nothing — that is exactly how the shipped bug looked:
    /// <c>@($x) | Where-Object { $_ }</c> opens and closes its array subexpression around the
    /// property, and the pipeline that follows unwraps the result again. The opening bracket has to
    /// close on the last character.
    /// </summary>
    private static bool WrapsEntireExpression(string rhs)
    {
        foreach (var cast in new[] { "[array]", "[string[]]" })
        {
            if (rhs.StartsWith(cast, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (!rhs.StartsWith("@(", StringComparison.Ordinal))
        {
            return false;
        }

        var depth = 0;

        for (var i = 1; i < rhs.Length; i++)
        {
            if (rhs[i] == '(')
            {
                depth++;
            }
            else if (rhs[i] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    // The subexpression closed here; it only counts if nothing follows it.
                    return i == rhs.Length - 1;
                }
            }
        }

        return false;
    }

    private static bool IsFlagged(string line)
    {
        var match = RiskyAssignment.Match(line);
        if (!match.Success)
        {
            return false;
        }

        var rhs = match.Groups["rhs"].Value.Trim();

        return IsBarePipelineIntoCollection(rhs)
               && !WrapsEntireExpression(rhs)
               && !rhs.StartsWith("[array]", StringComparison.OrdinalIgnoreCase)
               && !rhs.StartsWith("[array]", StringComparison.OrdinalIgnoreCase)
               && !rhs.StartsWith("[string[]]", StringComparison.OrdinalIgnoreCase);
    }
}
