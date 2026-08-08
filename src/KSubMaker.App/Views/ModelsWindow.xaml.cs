using System.Windows;
using KSubMaker.App.ViewModels;

namespace KSubMaker.App.Views;

/// <summary>
/// 모델 관리 dialog. Loads the catalog once the window is up and cancels any in-flight download when
/// it closes, so a half-finished transfer leaves only a resumable <c>.part</c> file behind.
/// </summary>
public partial class ModelsWindow : Window
{
    private readonly ModelsViewModel _viewModel;

    public ModelsWindow(ModelsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /// <summary>
    /// <see cref="async void"/> is forced by the event signature; the view model reports its own
    /// failures, and the catch here is the backstop that keeps one from escaping to the dispatcher.
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

    private void OnClosed(object? sender, EventArgs e)
    {
        Closed -= OnClosed;
        _viewModel.Dispose();
    }
}
