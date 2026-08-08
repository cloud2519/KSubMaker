using CommunityToolkit.Mvvm.ComponentModel;
using KSubMaker.App.Resources;

namespace KSubMaker.App.ViewModels;

/// <summary>
/// One entry of a <c>ComboBox</c>: the value that goes into <c>AppSettings</c> plus the Korean label
/// the user sees. Keeping the pair together is what stops a display string from leaking into the
/// persisted settings.
/// </summary>
public sealed class Option<T>(T value, string display)
{
    public T Value { get; } = value;

    public string Display { get; } = display;

    public override string ToString() => Display;
}

/// <summary>
/// A model entry in the 모델 선택 lists. <see cref="Id"/> matches <c>ModelDescriptor.Id</c>.
///
/// <para>Observable rather than a plain record because the install state arrives after the window is
/// already on screen: the combo boxes are built synchronously in the constructor, while
/// <c>IModelManager</c> has to touch the disk. Mutating <see cref="IsInstalled"/> in place lets the
/// labels fill in without rebuilding — and therefore without resetting — the user's selection.</para>
/// </summary>
public sealed partial class ModelOption : ObservableObject
{
    public ModelOption(string id, string name, bool? isInstalled = null)
    {
        Id = id;
        Name = name;
        _isInstalled = isInstalled;
    }

    public string Id { get; }

    /// <summary>Catalog display name, without the install-state suffix.</summary>
    public string Name { get; }

    /// <summary>True/false once checked; null while unknown, and permanently null for "자동".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Display))]
    private bool? _isInstalled;

    /// <summary>"이름 (설치됨)" / "이름 (미설치)", or just the name while the state is unknown.</summary>
    public string Display => IsInstalled switch
    {
        true => $"{Name} ({Strings.ModelStateInstalled})",
        false => $"{Name} ({Strings.ModelStateNotInstalled})",
        _ => Name
    };

    public override string ToString() => Display;
}
