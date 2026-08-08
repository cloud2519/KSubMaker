using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KSubMaker.App.ViewModels;

namespace KSubMaker.App.Views;

/// <summary>
/// Shell window. The logic here is closing coordination — genuinely a view concern, because the
/// queue and the Python worker have to be stopped asynchronously and WPF's
/// <see cref="Window.Closing"/> is synchronous — plus one input quirk that cannot be expressed as a
/// binding (<see cref="OnRowRightClick"/>).
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    private bool _shutdownStarted;
    private bool _readyToClose;

    public MainWindow(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>
    /// Makes a right-click select the row under the cursor.
    ///
    /// WPF's <c>DataGrid</c> does not do this on its own, so without it the context menu acts on
    /// whichever row was last left-clicked — which is the wrong file, silently. Not expressible as a
    /// binding: it is an input-routing detail of the control.
    /// </summary>
    private void OnRowRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGridRow row && !row.IsSelected)
        {
            row.IsSelected = true;
            row.Focus();
        }
    }

    /// <summary>
    /// Cancels the first close, runs the asynchronous teardown, then closes for real.
    ///
    /// <see cref="async void"/> is forced by the event signature; every path is inside the try/catch
    /// and the window closes even when teardown fails, so the user can never be trapped in a window
    /// that refuses to shut.
    /// </summary>
    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_readyToClose)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;

        if (_shutdownStarted)
        {
            return;
        }

        _shutdownStarted = true;

        try
        {
            await _viewModel.ShutdownAsync().ConfigureAwait(true);

            if (System.Windows.Application.Current is App app)
            {
                await app.ShutdownServicesAsync().ConfigureAwait(true);
            }
        }
        catch (Exception)
        {
            // ShutdownAsync already logged; a failure here must not block the close.
        }
        finally
        {
            _viewModel.Dispose();
            _readyToClose = true;

            // Teardown is asynchronous, so the window can already be gone by the time we get here.
            // ShutdownMode is OnMainWindowClose, and every application-shutdown path (a Windows
            // session-end, a second close request) closes windows with ignoreCancel=true — which
            // overrides the e.Cancel we set above and tears this window down while our first,
            // cancelled close was still awaiting. Calling Close() on an already-closing window
            // throws ("창을 닫는 중에는 ... Close ... 호출할 수 없습니다"). If the window is already on
            // its way out, the close we were about to request has effectively already happened.
            try
            {
                Close();
            }
            catch (InvalidOperationException)
            {
                // Window was already closing/closed by a racing application shutdown; nothing to do.
            }
        }
    }
}
