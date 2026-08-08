using CommunityToolkit.Mvvm.ComponentModel;

namespace KSubMaker.App.ViewModels;

/// <summary>
/// One 고유명사 사전 row: the source term and the Korean rendering it must always receive.
///
/// Observable rather than a plain record because the grid edits it in place; the collection is
/// folded back into <c>AppSettings.Glossary</c> only when 저장 is pressed, so a cancelled dialog
/// leaves the persisted dictionary untouched.
/// </summary>
public sealed partial class GlossaryEntryViewModel : ObservableObject
{
    public GlossaryEntryViewModel(string source, string target)
    {
        _source = source;
        _target = target;
    }

    /// <summary>Term as it appears in the source language.</summary>
    [ObservableProperty]
    private string _source = string.Empty;

    /// <summary>Fixed Korean rendering.</summary>
    [ObservableProperty]
    private string _target = string.Empty;
}
