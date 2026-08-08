using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace KSubMaker.UnitTests.Packaging;

/// <summary>
/// Guards the win32 manifest. It is the only place "매우 긴 경로" support and per-monitor DPI
/// awareness can be declared for a WPF process, and it is easy to lose in a csproj edit without
/// anything failing to build.
/// </summary>
public sealed class ApplicationManifestTests
{
    private static readonly XNamespace AsmV1 = "urn:schemas-microsoft-com:asm.v1";
    private static readonly XNamespace AsmV3 = "urn:schemas-microsoft-com:asm.v3";
    private static readonly XNamespace WindowsSettings2016 = "http://schemas.microsoft.com/SMI/2016/WindowsSettings";
    private static readonly XNamespace Compatibility = "urn:schemas-microsoft-com:compatibility.v1";

    private static XDocument Manifest()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "AppPackaging", "app.manifest");
        File.Exists(path).Should().BeTrue($"the manifest must be copied to the output ({path})");
        return XDocument.Load(path);
    }

    private static XElement WindowsSettings() =>
        Manifest().Root!
            .Elements(AsmV3 + "application")
            .Elements(AsmV3 + "windowsSettings")
            .Should().ContainSingle().Subject;

    [Fact]
    public void The_manifest_declares_long_path_awareness()
    {
        WindowsSettings()
            .Element(WindowsSettings2016 + "longPathAware")?.Value
            .Should().Be("true", "paths beyond MAX_PATH otherwise vanish from the scan without an error");
    }

    [Fact]
    public void The_manifest_declares_per_monitor_v2_dpi_awareness()
    {
        WindowsSettings()
            .Element(WindowsSettings2016 + "dpiAwareness")?.Value
            .Should().Be("PerMonitorV2");
    }

    [Fact]
    public void The_manifest_declares_the_supported_operating_systems()
    {
        var ids = Manifest().Root!
            .Elements(Compatibility + "compatibility")
            .Elements(Compatibility + "application")
            .Elements(Compatibility + "supportedOS")
            .Select(e => e.Attribute("Id")?.Value)
            .ToArray();

        // Windows 10 / 11. Without it the OS reports itself as Windows 8 to the process.
        ids.Should().Contain("{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}");
    }

    [Fact]
    public void The_application_never_asks_for_elevation()
    {
        var level = Manifest().Descendants(AsmV3 + "requestedExecutionLevel")
            .Should().ContainSingle().Subject;

        level.Attribute("level")?.Value.Should().Be("asInvoker");
    }

    [Fact]
    public void The_manifest_has_an_assembly_identity()
    {
        Manifest().Root!.Element(AsmV1 + "assemblyIdentity").Should().NotBeNull();
    }

    /// <summary>
    /// A manifest that is not referenced by the csproj is embedded nowhere and protects nothing.
    /// </summary>
    [Fact]
    public void The_shell_project_embeds_the_manifest()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "AppPackaging", "KSubMaker.App.csproj.xml");
        File.Exists(path).Should().BeTrue($"the shell project file must be copied to the output ({path})");

        var project = XDocument.Load(path);

        project.Descendants("ApplicationManifest")
            .Should().ContainSingle()
            .Which.Value.Trim().Should().Be("app.manifest");
    }

    /// <summary>
    /// The manifest comment has to keep pointing at the machine policy: enabling longPathAware alone
    /// does nothing without it, and a user reading only the manifest would draw the wrong conclusion.
    /// </summary>
    [Fact]
    public void The_manifest_records_that_the_machine_policy_is_also_required()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "AppPackaging", "app.manifest"));

        text.Should().Contain("LongPathsEnabled");
    }
}
