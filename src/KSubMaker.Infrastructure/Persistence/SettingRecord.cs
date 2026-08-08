namespace KSubMaker.Infrastructure.Persistence;

/// <summary>
/// One persisted setting. <see cref="Domain.Settings.AppSettings"/> is stored as flat key/value rows
/// rather than as a wide table, so adding a property to the settings object never needs a schema
/// migration — an unknown key is ignored on load and a missing key falls back to the C# default.
/// </summary>
public sealed class SettingRecord
{
    /// <summary>Property name of <see cref="Domain.Settings.AppSettings"/>, e.g. <c>BeamSize</c>.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Invariant-culture string form of the value; complex values are JSON.</summary>
    public string Value { get; set; } = string.Empty;
}
