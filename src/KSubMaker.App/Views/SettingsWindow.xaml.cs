using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;
using KSubMaker.App.ViewModels;

namespace KSubMaker.App.Views;

/// <summary>
/// 설정 dialog. The code-behind exists only to translate the view model's
/// <see cref="SettingsViewModel.CloseRequested"/> into <see cref="Window.DialogResult"/> — a window
/// cannot be closed from a view model without giving it a reference to the window.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;

        _viewModel.CloseRequested += OnCloseRequested;
        Loaded += OnLoaded;
        Closed += OnClosed;

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        AboutVersionText.Text = version is null ? "KSubMaker" : $"KSubMaker {version.ToString(3)}";
    }

    /// <summary>Opens a hyperlink in the default browser. Best-effort — a missing browser is not an error.</summary>
    private void OnNavigateToUri(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Nothing to do — the link simply does not open.
        }

        e.Handled = true;
    }

    /// <summary>
    /// <see cref="async void"/> is forced by the event signature; the body cannot throw because
    /// <see cref="SettingsViewModel.InitializeAsync"/> handles its own failures and reports them into
    /// the status line.
    /// </summary>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        try
        {
            await _viewModel.InitializeAsync().ConfigureAwait(true);
        }
        catch (Exception)
        {
            // Already logged and surfaced by the view model.
        }
    }

    private void OnCloseRequested(object? sender, bool saved)
    {
        try
        {
            // Assigning DialogResult also closes a modal dialog.
            DialogResult = saved;
        }
        catch (InvalidOperationException)
        {
            // Shown with Show() rather than ShowDialog(); a plain close is the right behaviour then.
            Close();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Closed -= OnClosed;
        _viewModel.CloseRequested -= OnCloseRequested;
    }
}
