using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using NewUOAM.Positioning.Relay;

namespace NewUOAM.App;

/// <summary>Admin panel for the multiplayer relay's rooms (see RelayServer/RelayAdminClient):
/// every room with who created it (player / admin) and when it was created and last used -
/// never passwords, player lists or positions (the server doesn't send them). Deletes any number
/// of rooms at once (by id; names aren't unique), switches player room creation on/off (the
/// anti-spam lever, user's request 2026-10-01), and creates admin rooms with a chosen password.
/// Talks to the relay address entered here with one-off requests, gated by the admin password.</summary>
public partial class AdminWindow : Window
{
    private sealed record RoomRow(RelayProtocol.AdminRoomInfo Info)
    {
        public string Name => Info.Name;
        public string Source => Info.CreatedByPlayer ? "hráč" : "admin";
        public Brush SourceBrush => Info.CreatedByPlayer ? Brushes.Khaki : Brushes.LightSkyBlue;
        public string Created => Info.CreatedUtc.ToLocalTime().ToString("d. M. yyyy H:mm");
        public string LastUsed => Describe(Info.LastUsedUtc);

        private static string Describe(DateTimeOffset utc)
        {
            var local = utc.ToLocalTime();
            int days = (DateTime.Today - local.Date).Days;
            return days switch
            {
                0 => "dnes " + local.ToString("H:mm"),
                1 => "včera",
                < 7 => $"před {days} dny",
                _ => local.ToString("d. M. yyyy"),
            };
        }
    }

    private bool _loaded;

    public AdminWindow(string serverAddress)
    {
        InitializeComponent();
        ServerAddressTextBox.Text = serverAddress;
        Loaded += (_, _) => AdminPasswordBox.Focus();
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

    private async void AdminPasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await RefreshAsync();
    }

    private async void RefreshRoomsButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (!TryParseAddress(out string host, out int port)) return;

        RefreshRoomsButton.IsEnabled = false;
        StatusTextBlock.Text = "Načítám místnosti…";
        try
        {
            var settings = await RelayAdminClient.GetCreationSettingsAsync(host, port, AdminPasswordBox.Password);
            ShowSettings(settings);
            if (!settings.Success && settings.Error is "BAD_ADMIN_PASSWORD" or "ADMIN_DISABLED" or "TIMEOUT")
            {
                StatusTextBlock.Text = $"Nepodařilo se načíst místnosti: {DescribeError(settings.Error)}";
                return;
            }

            var result = await RelayAdminClient.ListRoomDetailsAsync(host, port, AdminPasswordBox.Password);
            if (result.Success)
            {
                RoomsListBox.ItemsSource = result.Rooms.Select(r => new RoomRow(r)).ToList();
                _loaded = true;
                int players = result.Rooms.Count(r => r.CreatedByPlayer);
                StatusTextBlock.Text = $"Celkem {Rooms(result.Rooms.Count)}: od hráčů {players}, od admina {result.Rooms.Count - players}.";
            }
            else if (result.Error == "UNKNOWN_COMMAND")
            {
                // A server from before 2026-10-01: names only.
                var old = await RelayAdminClient.ListRoomsAsync(host, port, AdminPasswordBox.Password);
                RoomsListBox.ItemsSource = old.RoomNames.Select(n => new RoomRow(new RelayProtocol.AdminRoomInfo("", n, false, default, default))).ToList();
                StatusTextBlock.Text = "Server je starší verze: jen jména, mazání po jednom podle jména.";
            }
            else
            {
                StatusTextBlock.Text = $"Nepodařilo se načíst místnosti: {DescribeError(result.Error)}";
            }
        }
        finally
        {
            RefreshRoomsButton.IsEnabled = true;
            SelectRecentButton.IsEnabled = _loaded;
        }
    }

    private void ShowSettings(RelayAdminClient.CreationSettings settings)
    {
        PlayerCreationCheckBox.IsEnabled = settings.Success;
        if (!settings.Success)
        {
            PlayerCreationInfo.Text = settings.Error == "UNKNOWN_COMMAND"
                ? "Server je starší verze a zakládání hráči nezná."
                : "Nastavení nejde načíst.";
            return;
        }
        PlayerCreationCheckBox.IsChecked = settings.PlayersMayCreate;
        PlayerCreationInfo.Text = (settings.PlayersMayCreate
            ? "Zapnuto. Hráči zakládají v Online > Připojit k mapě, max. 3 místnosti za hodinu z jedné IP."
            : "Vypnuto. Nové místnosti zakládá jen admin.")
            + $" Hráčských místností: {settings.PlayerRooms} z {settings.MaxPlayerRooms}. Nepoužívané se mažou po 3 měsících.";
    }

    private async void PlayerCreationCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseAddress(out string host, out int port)) return;
        bool wanted = PlayerCreationCheckBox.IsChecked == true;
        PlayerCreationCheckBox.IsEnabled = false;
        var settings = await RelayAdminClient.SetPlayerCreationAsync(host, port, AdminPasswordBox.Password, wanted);
        ShowSettings(settings);
        StatusTextBlock.Text = settings.Success
            ? (settings.PlayersMayCreate ? "Zakládání místností hráči je zapnuté." : "Zakládání místností hráči je vypnuté.")
            : $"Nepodařilo se změnit nastavení: {DescribeError(settings.Error)}";
        if (!settings.Success) PlayerCreationCheckBox.IsChecked = !wanted;
    }

    private void RoomsListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        int n = RoomsListBox.SelectedItems.Count;
        DeleteRoomButton.IsEnabled = n > 0;
        DeleteRoomButton.Content = n > 1 ? $"Smazat vybrané ({n})" : "Smazat vybrané";
    }

    private void SelectRecentButton_Click(object sender, RoutedEventArgs e)
    {
        var since = DateTimeOffset.UtcNow.AddHours(-24);
        RoomsListBox.SelectedItems.Clear();
        foreach (var row in RoomsListBox.Items.OfType<RoomRow>().Where(r => r.Info.CreatedByPlayer && r.Info.CreatedUtc >= since))
            RoomsListBox.SelectedItems.Add(row);
        if (RoomsListBox.SelectedItems.Count == 0) StatusTextBlock.Text = "Za posledních 24 h hráči nic nezaložili.";
    }

    private async void DeleteRoomButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseAddress(out string host, out int port)) return;
        var rows = RoomsListBox.SelectedItems.OfType<RoomRow>().ToList();
        if (rows.Count == 0) return;

        string what = rows.Count == 1 ? $"místnost „{rows[0].Name}“" : Rooms(rows.Count);
        var confirm = MessageBox.Show(this,
            $"Smazat {what}? Všichni, kdo jsou v nich teď připojení, budou odpojeni.",
            "Smazat místnosti", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        DeleteRoomButton.IsEnabled = false;
        StatusTextBlock.Text = "Mažu…";
        if (rows.All(r => r.Info.Id.Length > 0))
        {
            var result = await RelayAdminClient.DeleteRoomsAsync(host, port, AdminPasswordBox.Password, rows.Select(r => r.Info.Id));
            await RefreshAsync();
            StatusTextBlock.Text = result.Success
                ? $"Smazáno: {Rooms(result.RoomsDeleted)}, odpojeno: {Players(result.PlayersKicked)}."
                : $"Nepodařilo se smazat: {DescribeError(result.Error)}";
        }
        else
        {
            // Older server without ids: by name.
            int kicked = 0;
            foreach (var row in rows)
                kicked += (await RelayAdminClient.DeleteRoomAsync(host, port, AdminPasswordBox.Password, row.Name)).PlayersKicked;
            await RefreshAsync();
            StatusTextBlock.Text = $"Smazáno, odpojeno: {Players(kicked)}.";
        }
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
                NewRoomNameTextBox.Text = "";
                NewRoomPasswordTextBox.Text = "";
                await RefreshAsync();
                StatusTextBlock.Text = $"Místnost „{roomName}“ vytvořena.";
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

    /// <summary>"1 místnost", "3 místnosti", "5 místností".</summary>
    private static string Rooms(int n) => n + (n == 1 ? " místnost" : n is >= 2 and <= 4 ? " místnosti" : " místností");

    private static string Players(int n) => n + (n == 1 ? " hráč" : n is >= 2 and <= 4 ? " hráči" : " hráčů");

    private static string DescribeError(string? error) => error switch
    {
        "BAD_ADMIN_PASSWORD" => "špatné admin heslo.",
        "ADMIN_DISABLED" => "server běží bez admin hesla (--admin-password).",
        "DUPLICATE_PASSWORD" => "toto heslo místnosti už existuje.",
        "INVALID_ARGS" => "neplatné zadání.",
        "NOT_FOUND" => "místnost už neexistuje.",
        "TIMEOUT" => "server neodpověděl (zkontroluj adresu/port).",
        "UNKNOWN_COMMAND" => "server je starší verze a tohle neumí.",
        _ => error ?? "neznámá chyba.",
    };
}
