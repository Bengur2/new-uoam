using System.Windows;
using NewUOAM.Positioning.Relay;

namespace NewUOAM.App;

/// <summary>Admin panel for the multiplayer relay's room system (see RelayServer/RelayAdminClient):
/// create a new password-protected room, and list existing rooms by name only - never player
/// lists, positions, or passwords (the relay server itself never sends that back, see
/// RelayServer.HandleAdminListAsync). Talks to whatever relay server address is entered here (not
/// the client - the admin operations are a separate one-off request/response, not a persistent
/// session), gated by the server's own admin password.</summary>
public partial class AdminWindow : Window
{
    public AdminWindow(string serverAddress)
    {
        InitializeComponent();
        ServerAddressTextBox.Text = serverAddress;
    }

    private bool TryParseAddress(out string host, out int port)
    {
        host = "";
        port = 0;
        string addr = ServerAddressTextBox.Text.Trim();
        int colon = addr.LastIndexOf(':');
        if (colon < 0 || !int.TryParse(addr[(colon + 1)..], out port))
        {
            StatusTextBlock.Text = "Neplatná adresa relay serveru (očekávám host:port).";
            return false;
        }
        host = addr[..colon];
        return true;
    }

    private async void CreateRoomButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseAddress(out string host, out int port)) return;

        string roomName = NewRoomNameTextBox.Text.Trim();
        string roomPassword = NewRoomPasswordTextBox.Text.Trim();
        if (string.IsNullOrEmpty(roomName) || string.IsNullOrEmpty(roomPassword))
        {
            StatusTextBlock.Text = "Vyplň jméno i heslo místnosti.";
            return;
        }

        CreateRoomButton.IsEnabled = false;
        StatusTextBlock.Text = "Vytvářím místnost…";
        try
        {
            var result = await RelayAdminClient.CreateRoomAsync(host, port, AdminPasswordBox.Password, roomName, roomPassword);
            if (result.Success)
            {
                StatusTextBlock.Text = $"Místnost '{roomName}' vytvořena.";
                NewRoomNameTextBox.Text = "";
                NewRoomPasswordTextBox.Text = "";
                await RefreshRoomsAsync();
            }
            else
            {
                StatusTextBlock.Text = $"Nepodařilo se vytvořit místnost: {DescribeError(result.Error)}";
            }
        }
        finally
        {
            CreateRoomButton.IsEnabled = true;
        }
    }

    private async void RefreshRoomsButton_Click(object sender, RoutedEventArgs e) => await RefreshRoomsAsync();

    private void RoomsListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        DeleteRoomButton.IsEnabled = RoomsListBox.SelectedItem is not null;

    private async void DeleteRoomButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseAddress(out string host, out int port)) return;
        if (RoomsListBox.SelectedItem is not string roomName) return;

        var confirm = MessageBox.Show(
            $"Smazat místnost '{roomName}'? Všichni aktuálně připojení hráči v ní budou odpojeni.",
            "Smazat místnost", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        DeleteRoomButton.IsEnabled = false;
        StatusTextBlock.Text = "Mažu místnost…";
        try
        {
            var result = await RelayAdminClient.DeleteRoomAsync(host, port, AdminPasswordBox.Password, roomName);
            if (result.Success)
            {
                StatusTextBlock.Text = $"Místnost '{roomName}' smazána ({result.PlayersKicked} hráč(ů) odpojeno).";
                await RefreshRoomsAsync();
            }
            else
            {
                StatusTextBlock.Text = $"Nepodařilo se smazat místnost: {DescribeError(result.Error)}";
            }
        }
        finally
        {
            DeleteRoomButton.IsEnabled = RoomsListBox.SelectedItem is not null;
        }
    }

    private async Task RefreshRoomsAsync()
    {
        if (!TryParseAddress(out string host, out int port)) return;

        RefreshRoomsButton.IsEnabled = false;
        StatusTextBlock.Text = "Načítám seznam místností…";
        try
        {
            var result = await RelayAdminClient.ListRoomsAsync(host, port, AdminPasswordBox.Password);
            if (result.Success)
            {
                RoomsListBox.ItemsSource = result.RoomNames;
                StatusTextBlock.Text = $"{result.RoomNames.Count} místnost(í).";
            }
            else
            {
                StatusTextBlock.Text = $"Nepodařilo se načíst seznam: {DescribeError(result.Error)}";
            }
        }
        finally
        {
            RefreshRoomsButton.IsEnabled = true;
        }
    }

    private static string DescribeError(string? error) => error switch
    {
        "BAD_ADMIN_PASSWORD" => "špatné admin heslo.",
        "ADMIN_DISABLED" => "server běží bez admin hesla (--admin-password).",
        "DUPLICATE_PASSWORD" => "toto heslo místnosti už existuje.",
        "INVALID_ARGS" => "neplatné jméno nebo heslo místnosti.",
        "NOT_FOUND" => "místnost s tímto jménem už neexistuje.",
        "TIMEOUT" => "server neodpověděl (zkontroluj adresu/port).",
        _ => error ?? "neznámá chyba.",
    };
}
