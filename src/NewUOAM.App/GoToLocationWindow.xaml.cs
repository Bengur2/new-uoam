using System.Windows;

namespace NewUOAM.App;

/// <summary>"Go to location..." (map context menu): asks for X/Y, validated against the facet's
/// size. The caller moves the map.</summary>
public partial class GoToLocationWindow : Window
{
    private readonly string _facetName;
    private readonly int _width, _height;

    public (int X, int Y)? Result { get; private set; }

    public GoToLocationWindow(int x, int y, string facetName, int widthTiles, int heightTiles)
    {
        InitializeComponent();
        _facetName = facetName;
        _width = widthTiles;
        _height = heightTiles;
        XTextBox.Text = x.ToString();
        YTextBox.Text = y.ToString();
        Loaded += (_, _) => { XTextBox.Focus(); XTextBox.SelectAll(); };
    }

    private void Input_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) ErrorText.Visibility = Visibility.Collapsed;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        string? error = null;
        if (!int.TryParse(XTextBox.Text.Trim(), out int x) || !int.TryParse(YTextBox.Text.Trim(), out int y))
            error = "X a Y musí být celá čísla.";
        else if (x < 0 || y < 0 || x >= _width || y >= _height)
            error = $"Pozice je mimo {_facetName} (0-{_width - 1}, 0-{_height - 1}).";
        else
            Result = (x, y);

        if (error is not null)
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        DialogResult = true;
    }
}
