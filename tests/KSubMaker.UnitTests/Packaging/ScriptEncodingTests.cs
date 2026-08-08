using System.Text;
using FluentAssertions;
using Xunit;

namespace KSubMaker.UnitTests.Packaging;

/// <summary>
/// Guards the encoding of every shipped PowerShell and Inno Setup script.
///
/// Why this test exists: Windows PowerShell 5.1 — still the default shell on Windows 10/11 — does
/// **not** assume UTF-8. Without a byte-order mark it decodes a <c>.ps1</c> using the system ANSI
/// code page, which on a Korean install is CP949. CP949 is double-byte: a lead byte in 0x81–0xFE
/// consumes the byte after it. A UTF-8 Hangul character is three bytes, so across a run of Korean
/// text the pairing drifts by one and eventually swallows the following ASCII byte — including the
/// closing quote of a string literal. The script then fails to *parse*, with errors pointing at
/// lines far away from the real problem:
///
/// <code>
///   Write-Host ("{0}" -f '실시간 대비', $ratio)   →   '?ㅼ떆媛??鍮?, $ratio)
///   문 블록 또는 형식 정의에 닫는 '}'가 없습니다.
/// </code>
///
/// This shipped broken once. PowerShell 7 (<c>pwsh</c>) reads UTF-8 without a BOM quite happily, so
/// parse-checking the scripts on a build agent does not catch it; only the byte check does.
///
/// Inno Setup 6 has the same rule: a <c>.iss</c> is only read as UTF-8 when it starts with a BOM.
/// </summary>
public sealed class ScriptEncodingTests
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private static string ScriptDirectory => Path.Combine(AppContext.BaseDirectory, "ShippedScripts");

    private static IReadOnlyList<string> ScriptFileNames() =>
        Directory.EnumerateFiles(ScriptDirectory)
            .Where(p => p.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)
                        || p.EndsWith(".iss", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .OfType<string>()
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

    public static TheoryData<string> ShippedScripts()
    {
        var data = new TheoryData<string>();

        foreach (var name in ScriptFileNames())
        {
            data.Add(name);
        }

        return data;
    }

    [Fact]
    public void The_shipped_scripts_are_actually_copied_next_to_the_tests()
    {
        Directory.Exists(ScriptDirectory).Should().BeTrue(
            $"the csproj copies scripts/*.ps1 and installer/*.iss into {ScriptDirectory}");

        // A silently empty glob would make every [Theory] below vacuously pass.
        ShippedScripts().Count.Should().BeGreaterThan(5,
            "the repository ships several PowerShell scripts plus the Inno Setup script");
    }

    [Theory]
    [MemberData(nameof(ShippedScripts))]
    public void Every_shipped_script_starts_with_a_utf8_bom(string fileName)
    {
        var bytes = File.ReadAllBytes(Path.Combine(ScriptDirectory, fileName));

        bytes.Length.Should().BeGreaterThan(3, "an empty script would not be worth shipping");

        bytes.Take(3).Should().Equal(Utf8Bom,
            $"{fileName} contains Korean text and Windows PowerShell 5.1 / Inno Setup fall back to " +
            "the ANSI code page (CP949) without a BOM, which corrupts string literals and breaks parsing");
    }

    [Theory]
    [MemberData(nameof(ShippedScripts))]
    public void Every_shipped_script_is_valid_utf8(string fileName)
    {
        var bytes = File.ReadAllBytes(Path.Combine(ScriptDirectory, fileName));
        var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        var decode = () => strict.GetString(bytes, Utf8Bom.Length, bytes.Length - Utf8Bom.Length);

        decode.Should().NotThrow<DecoderFallbackException>(
            $"{fileName} must be UTF-8; a BOM in front of CP949 bytes would be worse than no BOM at all");
    }

    [Theory]
    [MemberData(nameof(ShippedScripts))]
    public void Every_shipped_script_has_balanced_quotes_when_read_as_utf8(string fileName)
    {
        var body = Body(fileName);

        CountLinesWithUnpairedQuotes(Encoding.UTF8.GetString(body))
            .Should().Be(0, $"{fileName} must tokenise cleanly when the BOM is honoured");
    }

    /// <summary>
    /// Demonstrates that the BOM is load-bearing rather than cosmetic: read as CP949, the shipped
    /// scripts really do lose the closing quote of a Korean string literal.
    ///
    /// Asserted across the whole set, not per file, because whether the drift happens to swallow a
    /// quote depends on where the Korean runs sit relative to the ASCII around them — some files are
    /// corrupted into nonsense without breaking quote pairing specifically. One demonstration is
    /// enough to prove the mechanism; requiring it of every file would be a flaky assertion about
    /// byte alignment.
    /// </summary>
    [Fact]
    public void Read_as_cp949_the_scripts_lose_string_delimiters()
    {
        var damaged = new List<string>();

        foreach (var fileName in ScriptFileNames())
        {
            if (!fileName.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var body = Body(fileName);
            if (!body.Any(b => b >= 0x80))
            {
                continue; // Pure ASCII: nothing for CP949 to corrupt.
            }

            if (CountLinesWithUnpairedQuotes(SimulateCp949(body)) > 0)
            {
                damaged.Add(fileName);
            }
        }

        damaged.Should().NotBeEmpty(
            "at least one shipped script must still show the corruption the BOM prevents — " +
            "if none do, this guard has quietly stopped testing anything");
    }

    private static byte[] Body(string fileName)
    {
        var bytes = File.ReadAllBytes(Path.Combine(ScriptDirectory, fileName));
        return bytes.Skip(Utf8Bom.Length).ToArray();
    }

    /// <summary>
    /// Models the part of CP949 that does the damage: a lead byte in 0x81–0xFE always consumes the
    /// following byte, whatever it is. Trailing bytes of a UTF-8 sequence are never ASCII, but the
    /// three-bytes-per-Hangul against two-bytes-per-CP949-char mismatch drifts the alignment until a
    /// lead byte lands one position before an ASCII character and eats it.
    /// </summary>
    private static string SimulateCp949(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length);

        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] is >= 0x81 and <= 0xFE && i + 1 < bytes.Length)
            {
                builder.Append('�');
                i++; // The next byte is consumed as the trail byte — even if it is ' or }.
            }
            else
            {
                builder.Append((char)bytes[i]);
            }
        }

        return builder.ToString();
    }

    private static int CountLinesWithUnpairedQuotes(string text) =>
        text.Split('\n').Count(line => line.Count(c => c == '\'') % 2 != 0);
}
