using System.IO;
using System.Windows;
using KSubMaker.App.Resources;
using Microsoft.Win32;

namespace KSubMaker.App.Services;

/// <summary>
/// WPF implementation of <see cref="IDialogService"/>.
///
/// Uses <see cref="OpenFolderDialog"/> (WPF, .NET 8+) rather than the WinForms
/// <c>FolderBrowserDialog</c>, which is why this project can keep <c>UseWindowsForms=false</c>.
/// </summary>
public sealed class DialogService : IDialogService
{
    public string? PickFolder(string title, string? initialDirectory = null)
    {
        var dialog = new OpenFolderDialog
        {
            Title = string.IsNullOrWhiteSpace(title) ? Strings.SelectFolderDialogTitle : title,
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory))
        {
            try
            {
                if (Directory.Exists(initialDirectory))
                {
                    dialog.InitialDirectory = initialDirectory;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                // A stale or malformed last-used folder must not stop the picker from opening.
            }
        }

        return dialog.ShowDialog(Owner()) == true ? dialog.FolderName : null;
    }

    public void ShowInformation(string message, string? title = null) =>
        Show(message, title ?? Strings.DialogTitleInfo, MessageBoxImage.Information);

    public void ShowWarning(string message, string? title = null) =>
        Show(message, title ?? Strings.DialogTitleWarning, MessageBoxImage.Warning);

    public void ShowError(string message, string? title = null) =>
        Show(message, title ?? Strings.DialogTitleError, MessageBoxImage.Error);

    public bool Confirm(string message, string? title = null)
    {
        var owner = Owner();
        var caption = title ?? Strings.DialogTitleConfirm;

        var result = owner is null
            ? MessageBox.Show(message, caption, MessageBoxButton.YesNo, MessageBoxImage.Question)
            : MessageBox.Show(owner, message, caption, MessageBoxButton.YesNo, MessageBoxImage.Question);

        return result == MessageBoxResult.Yes;
    }

    public string? PromptText(string title, string message, string? initialValue = null, bool multiline = false)
    {
        var window = new Views.TextPromptWindow(
            string.IsNullOrWhiteSpace(title) ? Strings.DialogTitleInfo : title,
            message,
            initialValue,
            multiline)
        {
            Owner = Owner()
        };

        return window.ShowDialog() == true ? window.Value : null;
    }

    public SubtitleSourceOption? PickSubtitleSource(
        string title,
        string message,
        IReadOnlyList<SubtitleSourceOption> options,
        int selectedIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Count == 0)
        {
            // Nothing to pick from is a caller bug, but showing an empty modal would be worse.
            return null;
        }

        var window = new Views.SubtitleSourceWindow(message, options, selectedIndex)
        {
            Owner = Owner()
        };

        if (!string.IsNullOrWhiteSpace(title))
        {
            window.Title = title;
        }

        return window.ShowDialog() == true ? window.SelectedOption : null;
    }

    private static void Show(string message, string caption, MessageBoxImage icon)
    {
        var owner = Owner();

        if (owner is null)
        {
            MessageBox.Show(message, caption, MessageBoxButton.OK, icon);
            return;
        }

        MessageBox.Show(owner, message, caption, MessageBoxButton.OK, icon);
    }

    /// <summary>
    /// The active window, falling back to the main window. Parenting matters: an unowned message box
    /// can end up behind the application, which looks like a hang to the user.
    /// </summary>
    private static Window? Owner()
    {
        var application = System.Windows.Application.Current;
        if (application is null)
        {
            return null;
        }

        foreach (Window window in application.Windows)
        {
            if (window.IsActive)
            {
                return window;
            }
        }

        return application.MainWindow;
    }
}
