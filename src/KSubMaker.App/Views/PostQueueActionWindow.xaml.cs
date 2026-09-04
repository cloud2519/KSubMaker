using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using KSubMaker.App.Resources;

namespace KSubMaker.App.Views;

/// <summary>
/// The cancellable countdown shown before a 큐 완료 후 동작 (절전 / 최대 절전 / 시스템 종료).
///
/// <para>Self-contained on purpose: no view model, no bindings, no dependency graph. It is created
/// with <c>new</c> at the moment the queue drains — a container round trip would only add moving
/// parts to something that shows two lines of text and a timer.</para>
///
/// <para><see cref="Window.DialogResult"/> is <c>true</c> when the countdown finished or the user
/// pressed 지금 실행, and <c>false</c> for every other exit (취소, Esc, the close box).</para>
/// </summary>
public partial class PostQueueActionWindow : Window
{
    private readonly string _actionName;
    private readonly DispatcherTimer _timer;
    private int _remainingSeconds;

    /// <param name="actionName">Already-localised name of the action, for the heading.</param>
    /// <param name="seconds">Countdown length. Clamped to at least one second.</param>
    public PostQueueActionWindow(string actionName, int seconds = 30)
    {
        _actionName = actionName;
        _remainingSeconds = Math.Max(1, seconds);

        InitializeComponent();

        HeadingText.Text = string.Format(
            CultureInfo.CurrentCulture, Strings.PostQueueActionHeadingFormat, _actionName);
        RenderCountdown();

        _timer = new DispatcherTimer(
            TimeSpan.FromSeconds(1),
            DispatcherPriority.Normal,
            OnTick,
            Dispatcher);

        Loaded += (_, _) => _timer.Start();
        Closed += OnClosed;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _remainingSeconds--;

        if (_remainingSeconds <= 0)
        {
            _timer.Stop();
            Complete(proceed: true);
            return;
        }

        RenderCountdown();
    }

    private void RenderCountdown() =>
        CountdownText.Text = string.Format(
            CultureInfo.CurrentCulture, Strings.PostQueueActionCountdownFormat, _remainingSeconds);

    private void OnProceedClick(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        Complete(proceed: true);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        Complete(proceed: false);
    }

    /// <summary>
    /// Sets the result once and closes. Guarded because both the tick and a button click can race to
    /// finish the dialog, and assigning <see cref="Window.DialogResult"/> after the window has
    /// closed throws.
    /// </summary>
    private void Complete(bool proceed)
    {
        if (!IsLoaded)
        {
            return;
        }

        try
        {
            DialogResult = proceed;
        }
        catch (InvalidOperationException)
        {
            // Already closing from the other path.
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Closed -= OnClosed;
        _timer.Stop();
    }
}
