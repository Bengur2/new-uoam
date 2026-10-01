using System.Windows;
using NewUOAM.Positioning.Relay;

namespace NewUOAM.App;

/// <summary>A player creates a room. DialogResult = true and <see cref="Password"/> set when the
/// player chose "Použít heslo".</summary>
public partial class CreateRoomWindow : Window
{
    private readonly string _serverAddress;

    public string? Password { get; private set; }

    public CreateRoomWindow(string serverAddress)
    {
        InitializeComponent();
        _serverAddress = serverAddress;
        Loaded += (_, _) => RoomNameTextBox.Focus();
    }

    private void RoomNameTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        CreateButton.IsEnabled = RelayProtocol.CleanRoomName(RoomNameTextBox.Text) is not null;

    private async void CreateButton_Click(object sender, RoutedEventArgs e)
    {
        int colon = _serverAddress.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(_serverAddress[(colon + 1)..], out int port))
        {
            ShowError("Adresa serveru v Online > Připojit k mapě není platná (host:port).");
            return;
        }

        CreateButton.IsEnabled = false;
        RoomNameTextBox.IsEnabled = false;
        ErrorText.Visibility = Visibility.Collapsed;
        try
        {
            var result = await RelayAdminClient.CreatePlayerRoomAsync(_serverAddress[..colon], port, RoomNameTextBox.Text);
            if (result.Success)
            {
                Password = result.Password;
                DoneTitle.Text = Loc.F("Cr_Done", RelayProtocol.CleanRoomName(RoomNameTextBox.Text));
                PasswordTextBox.Text = result.Password;
                RequestPanel.Visibility = Visibility.Collapsed;
                DonePanel.Visibility = Visibility.Visible;
                PasswordTextBox.Focus();
                PasswordTextBox.SelectAll();
                return;
            }
            ShowError(result.Error switch
            {
                "DISABLED" => "Zakládání místností je teď vypnuté. Zkus to později nebo se obrať na admina mapy.",
                "RATE_LIMIT" => "Z tvého připojení už vznikly 3 místnosti za poslední hodinu. Zkus to později.",
                "FULL" => "Na serveru je moc místností. Obrať se na admina mapy.",
                "INVALID_NAME" => "Zadej jméno místnosti.",
                "TIMEOUT" => "Server neodpověděl. Zkontroluj adresu serveru v Online > Připojit k mapě.",
                _ => $"Místnost se nepodařilo založit ({result.Error}).",
            });
        }
        finally
        {
            if (Password is null)
            {
                RoomNameTextBox.IsEnabled = true;
                CreateButton.IsEnabled = RelayProtocol.CleanRoomName(RoomNameTextBox.Text) is not null;
            }
        }
    }

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(PasswordTextBox.Text);
            CopyButton.SetResourceReference(ContentProperty, "Cr_Copied");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Clipboard briefly locked by another app; the text box is selectable anyway.
        }
    }

    private void UseButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
