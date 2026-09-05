namespace KSubMaker.App.Services;

/// <summary>
/// Everything the view models need from the user that requires a window.
///
/// View models never touch <c>MessageBox</c> or a file dialog directly: those types are the reason a
/// view model would otherwise be untestable and would pin the code to WPF.
/// </summary>
public interface IDialogService
{
    /// <summary>
    /// Shows the Vista folder picker. Returns null when the user cancels.
    /// </summary>
    /// <param name="title">Dialog caption.</param>
    /// <param name="initialDirectory">Starting folder; ignored when it does not exist.</param>
    string? PickFolder(string title, string? initialDirectory = null);

    void ShowInformation(string message, string? title = null);

    void ShowWarning(string message, string? title = null);

    void ShowError(string message, string? title = null);

    /// <summary>Yes/No question. Returns true only for an explicit "예".</summary>
    bool Confirm(string message, string? title = null);

    /// <summary>
    /// One-line (or multi-line) text prompt. Returns the entered string, or null when cancelled —
    /// which the caller must treat as "do nothing", never as an empty string.
    /// </summary>
    string? PromptText(string title, string message, string? initialValue = null, bool multiline = false);

    /// <summary>
    /// Modal 자막 원본 picker. Returns the chosen option, or null when the user cancelled — which the
    /// caller must treat as "change nothing", never as "use the first option".
    /// </summary>
    /// <param name="title">Window caption.</param>
    /// <param name="message">One-line explanation shown above the list, usually the file name.</param>
    /// <param name="options">Never empty; the first entry is the MVP core path.</param>
    /// <param name="selectedIndex">Index pre-selected when the dialog opens.</param>
    SubtitleSourceOption? PickSubtitleSource(
        string title,
        string message,
        IReadOnlyList<SubtitleSourceOption> options,
        int selectedIndex = 0);
}
