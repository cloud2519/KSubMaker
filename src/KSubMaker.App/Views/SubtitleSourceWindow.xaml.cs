using System.Windows;
using KSubMaker.App.Services;

namespace KSubMaker.App.Views;

/// <summary>
/// 자막 원본 picker: a modal single-choice list over the probed audio and subtitle tracks.
///
/// Deliberately without a view model. It owns no state beyond "which row is highlighted", has no
/// services to call and no validation to run; a view model here would be a second file that only
/// forwards a <see cref="SubtitleSourceOption"/>. The list is supplied fully formed by
/// <see cref="DialogService"/>, so this class never has to know how an option was built.
/// </summary>
public partial class SubtitleSourceWindow : Window
{
    public SubtitleSourceWindow(
        string message,
        IReadOnlyList<SubtitleSourceOption> options,
        int selectedIndex)
    {
        ArgumentNullException.ThrowIfNull(options);

        InitializeComponent();

        MessageText.Text = message;
        OptionList.ItemsSource = options;

        // Always land on a valid row: an empty selection would make 확인 a no-op that looks broken.
        OptionList.SelectedIndex = options.Count == 0
            ? -1
            : Math.Clamp(selectedIndex, 0, options.Count - 1);
    }

    /// <summary>The chosen option, or null when the dialog was cancelled.</summary>
    public SubtitleSourceOption? SelectedOption { get; private set; }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        if (OptionList.SelectedItem is not SubtitleSourceOption option)
        {
            return;
        }

        SelectedOption = option;
        DialogResult = true;
    }
}
