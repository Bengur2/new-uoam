using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Shell;
using System.Windows.Media;

namespace NewUOAM.App;

/// <summary>One chat line. A player message shows "Sender: " + text, both in the sender's color;
/// a system notice (someone joined/left) has no sender and shows gray italics.</summary>
public sealed record ChatMessageViewModel(string? Sender, string Text, Brush Brush)
{
    private static readonly Brush SystemBrush = MakeFrozen(Color.FromRgb(0x9A, 0x9A, 0x9A));

    /// <summary>When the line arrived on this map (shown as "HH:mm" in front of it).</summary>
    public DateTime Time { get; init; } = DateTime.Now;
    public string TimeText => Time.ToString("HH:mm") + "  ";
    public Brush TimeBrush => SystemBrush;

    public string SenderPrefix => Sender is null ? "" : Sender + ": ";
    public FontStyle FontStyle => Sender is null ? FontStyles.Italic : FontStyles.Normal;

    public static ChatMessageViewModel System(string text) => new(null, text, SystemBrush);

    private static Brush MakeFrozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>A row of the chat's player list (the 👥 button): name in the player's color and
/// where they are relative to you ("120 tiles NorthEast").</summary>
public sealed record ChatPlayerInfo(string Name, Brush Brush, string Detail, bool IsMe);

/// <summary>Room-scoped chat window (see RelayProtocol.ChatSendTag / RelayMultiplayerClient).
/// Opened by MainWindow when the user clicks into the map and starts typing while the relay is
/// connected (see MainWindow.MapBorder_PreviewTextInput), or by clicking the unread counter.
/// It only shows MainWindow's in-memory history (MainWindow.AddChatLine), which holds everything
/// since connecting, whether this window was open or not (user's request 2026-09-29). Nothing is
/// written to disk anywhere: the history is dropped on disconnect and when the map closes, and
/// the 🗑 button clears it for this map only.</summary>
public partial class ChatWindow : Window
{
    private readonly ObservableCollection<ChatMessageViewModel> _messages;

    /// <summary>Raised when the user submits text (Enter) - MainWindow wires
    /// this to RelayMultiplayerClient.SendChatMessage rather than this window knowing anything
    /// about the relay client itself.</summary>
    public event EventHandler<string>? MessageSubmitted;

    public ChatWindow(ObservableCollection<ChatMessageViewModel> messages)
    {
        InitializeComponent();
        _messages = messages;
        MessagesList.ItemsSource = messages;
        messages.CollectionChanged += OnMessagesChanged;
        Closed += (_, _) => messages.CollectionChanged -= OnMessagesChanged;
        Loaded += (_, _) => MessagesScroll.ScrollToEnd();
    }

    private void OnMessagesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        MessagesScroll.ScrollToEnd();

    /// <summary>Supplies the players for the 👥 list (MainWindow.DescribeRoomPlayers), asked each
    /// time the list opens.</summary>
    public Func<IReadOnlyList<ChatPlayerInfo>>? PlayersProvider { get; set; }

    // When the popup last closed. A click on 👥 while the list is open first closes it
    // (StaysOpen=False closes on any outside mouse-down), then arrives as a Click that would
    // reopen it straight away; a Click right after a close is that same click, so it's ignored -
    // the button toggles the list (user's report 2026-09-29).
    private long _playersPopupClosedAt = long.MinValue / 2;

    private void PlayersPopup_Closed(object? sender, EventArgs e) => _playersPopupClosedAt = Environment.TickCount64;

    private void PlayersButton_Click(object sender, RoutedEventArgs e)
    {
        if (PlayersPopup.IsOpen)
        {
            PlayersPopup.IsOpen = false;
            return;
        }
        if (Environment.TickCount64 - _playersPopupClosedAt < 300) return;

        var players = PlayersProvider?.Invoke() ?? [];
        PlayersList.ItemsSource = players;
        PlayersEmptyText.Visibility = players.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PlayersPopup.IsOpen = true;
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _messages.Clear();
        ChatInputTextBox.Focus();
    }

    /// <summary>Focuses the input box, optionally seeding it with the character that triggered
    /// the window to open in the first place (see MainWindow.MapBorder_PreviewTextInput) - without
    /// this, the keystroke that opened the window would otherwise be silently lost, which would
    /// feel broken ("I typed 'hi' and only 'i' showed up").</summary>
    public void FocusInput(string? seedText = null)
    {
        if (!string.IsNullOrEmpty(seedText)) ChatInputTextBox.Text += seedText;
        ChatInputTextBox.CaretIndex = ChatInputTextBox.Text.Length;
        ChatInputTextBox.Focus();
    }

    // ---- Compact mode (user's request 2026-09-25), the chat's counterpart of the map's
    // double-click "map only" mode: double-click the message list to drop the title bar and make
    // the chat always on top; the message list, input box and clear button stay. Its always-on-top
    // is the chat's own - independent of whether the map is always on top. ----

    private bool _compact;

    private void MessagesBorder_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Preview (not the bubbling event): the ScrollViewer marks MouseLeftButtonDown handled.
        // Clicks on its scrollbar are left alone so scrolling still works.
        if (IsInsideScrollBar(e.OriginalSource as DependencyObject)) return;

        if (e.ClickCount == 2)
        {
            SetCompact(!_compact);
            e.Handled = true;
        }
        else if (_compact && e.ClickCount == 1 && e.LeftButton == MouseButtonState.Pressed)
        {
            // No title bar to drag by in compact mode - drag the window by its message list.
            try { DragMove(); }
            catch (InvalidOperationException) { /* button already released - ignore */ }
        }
    }

    /// <summary>Also called by MainWindow to open the chat already compact while the map is in
    /// map-only mode (the map is always on top then and would hide a normal window).</summary>
    public void SetCompact(bool compact)
    {
        _compact = compact;
        // Order matters (two crashes, user reports 2026-09-28). The default Window style has a
        // separate template for CanResizeWithGrip, so changing ResizeMode swaps the window's
        // template, and WindowChromeWorker must never see that swap:
        //  - with a chrome set, a template change queues a deferred _FixupTemplateIssues, which
        //    throws NullReferenceException if the chrome is gone by the time it runs;
        //  - adding/removing a chrome while the new template isn't applied yet throws in
        //    GetChild(window, 0).
        // So the chrome is removed first when leaving compact mode, and added last (after an
        // explicit ApplyTemplate) when entering it.
        if (!compact) WindowChrome.SetWindowChrome(this, null);
        WindowStyle = compact ? WindowStyle.None : WindowStyle.SingleBorderWindow;
        // CanResizeWithGrip: a borderless window still gets a resize grip bottom-right.
        ResizeMode = compact ? ResizeMode.CanResizeWithGrip : ResizeMode.CanResize;
        ApplyTemplate();
        // A resizable WindowStyle.None window still gets a thin white strip along its top edge
        // (the leftover system frame - the user saw it). A custom WindowChrome with no caption
        // and no glass frame replaces that frame entirely while keeping edge resizing.
        if (compact)
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false,
            });
        Topmost = compact;
        FocusInput();
    }

    private static bool IsInsideScrollBar(DependencyObject? element)
    {
        for (var current = element; current is not null; current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is System.Windows.Controls.Primitives.ScrollBar) return true;
        return false;
    }

    private void ChatInputTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SubmitCurrentText();
            e.Handled = true;
        }
    }

    private void SubmitCurrentText()
    {
        string text = ChatInputTextBox.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;
        MessageSubmitted?.Invoke(this, text);
        ChatInputTextBox.Text = "";
    }
}
