using System.Windows;

namespace NewUOAM.App;

/// <summary>Asks for one line of text. <c>validate</c> returns an error to show, or null when the
/// (trimmed) text is fine.</summary>
public partial class TextPromptWindow : Window
{
    private readonly Func<string, string?> _validate;

    public string? Result { get; private set; }

    public TextPromptWindow(string title, string prompt, string initial, Func<string, string?> validate)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        InputTextBox.Text = initial;
        _validate = validate;
        Loaded += (_, _) => { InputTextBox.Focus(); InputTextBox.SelectAll(); };
    }

    private void InputTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (IsLoaded) ErrorText.Visibility = Visibility.Collapsed;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        string text = InputTextBox.Text.Trim();
        if (_validate(text) is { } error)
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        Result = text;
        DialogResult = true;
    }
}
