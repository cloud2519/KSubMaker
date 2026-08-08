using System.ComponentModel;
using System.Windows;
using KSubMaker.App.ViewModels;

namespace KSubMaker.App.Views;

/// <summary>
/// 로그 보기 window. Starts the tail loop when the window appears and cancels it when the window
/// closes, so a closed window stops touching the log file immediately.
///
/// Closing is deliberately synchronous: this window can also be closed by
/// <c>Application.Shutdown()</c>, which ignores a cancelled close, so an async
/// "cancel then close again" dance here would be both pointless and fragile.
/// </summary>
public partial class LogWindow : Window
{
    private readonly LogViewModel _viewModel;

    public LogWindow(LogViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        _viewModel.Start();
    }

    /// <summary>
    /// Keeps the newest lines in view. A tail that silently stays scrolled to the top is worse than
    /// no tail at all, and auto-scroll cannot be expressed as a binding.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogViewModel.LogText))
        {
            LogTextBox.ScrollToEnd();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Closed -= OnClosed;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.Dispose();
    }
}
