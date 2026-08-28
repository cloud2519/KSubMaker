using System.Windows;

namespace KSubMaker.App.Views;

/// <summary>
/// A one-field text prompt (파일 이름 바꾸기, 메모 편집). No view model: it holds a single string and
/// has nothing to validate — the caller decides what an empty result means.
/// </summary>
public partial class TextPromptWindow : Window
{
    public TextPromptWindow(string title, string message, string? initialValue, bool multiline)
    {
        InitializeComponent();

        Title = title;
        MessageText.Text = message;
        Input.Text = initialValue ?? string.Empty;
        Input.AcceptsReturn = multiline;

        if (multiline)
        {
            Height = 260;
            Input.MinLines = 3;
        }

        Loaded += (_, _) =>
        {
            Input.Focus();
            Input.SelectAll();
        };
    }

    /// <summary>The entered text, or null when the dialog was cancelled.</summary>
    public string? Value { get; private set; }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        Value = Input.Text;
        DialogResult = true;
    }
}
