using System.IO;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;
using AirlyClient.Network;

namespace AirlyClient;

public partial class MainWindow : Window
{
    private readonly HttpClient _http = new();
    private readonly AirportDataService _airportData;
    private readonly ObservableCollection<TrackedFlight> _trackedFlights = new();
    private readonly AppSettings _settings;
    private bool _connected;
    private TrackedFlight? _selectedFlight;
    private SimBriefFlightPlan? _flightPlan;
    private readonly DispatcherTimer _metricsTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly ModelMatchingInstaller _modelInstaller;
    private readonly UpdateService _updateService;
    private readonly List<TrackerAircraft> _liveAircraft = new();
    private readonly DispatcherTimer _trackingTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private bool _trackingRequestRunning;
    private UpdateInfo? _availableUpdate;
    private bool _updatePromptShown;
    private bool _updateIsMandatory;
    private string _pendingThemeMode = "Dark";
    private string _pendingAccentColor = "#80858B";
    private bool _aiTitleRequestRunning;
    private string? _communityFolder;
    private bool _metricsRequestRunning;
    private int _renderFrames;
    private long _lastFpsTick;
    private readonly ObservableCollection<AiChatSession> _aiChats = new();
    private AiChatSession? _activeAiChat;
    private readonly string _aiChatFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Airly", "ai-chats.json");

    public MainWindow()
    {
        InitializeComponent();
        _settings = AppSettings.Load();
        _airportData = new AirportDataService(_http);
        _modelInstaller = new ModelMatchingInstaller(_http);
        _updateService = new UpdateService(_http);
        TrackingGrid.ItemsSource = _trackedFlights;
        LoadSettingsIntoUi();
        InitializeTheme();
        InitializeEmojiPicker();
        RenderColorWheel();
        LoadDemoUiState();
        InitializeClientMetrics();
        _ = InitializeTrackingMapAsync();
        InitializeModelMatching();
        TrackingGrid.ItemsSource = _trackedFlights;
        _trackingTimer.Tick += async (_, _) => await RefreshLiveTrackingAsync();
        _trackingTimer.Start();
        _ = RefreshLiveTrackingAsync();
        Closed += (_, _) => _settings.Save();
        Closed += (_, _) => CompositionTarget.Rendering -= CompositionTarget_Rendering;
        Closed += (_, _) => _trackingTimer.Stop();
        VersionLabel.Text = "Airly Client " + ClientConfig.Version;
        InitializeAiChats();
        Loaded += async (_, _) => await CheckForUpdatesAsync();
    }

    private void AddAiMessage(string sender, string message)
    {
        EnsureActiveAiChat();
        _activeAiChat!.Messages.Add(new AiChatMessage { Sender = sender, Text = message });
        SaveAiChats();
        RenderActiveAiChat();
    }



    private StackPanel RenderAiMarkdown(string markdown)
    {
        var panel = new StackPanel();
        var inCode = false;
        var code = new List<string>();
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Trim().StartsWith(new string('`', 3), StringComparison.Ordinal))
            {
                if (inCode)
                {
                    panel.Children.Add(new Border
                    {
                        Background = new SolidColorBrush(Color.FromRgb(17, 20, 24)),
                        CornerRadius = new CornerRadius(10),
                        Padding = new Thickness(13),
                        Margin = new Thickness(0, 5, 0, 12),
                        Child = new TextBlock
                        {
                            Text = string.Join(Environment.NewLine, code),
                            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                            FontSize = 12,
                            Foreground = (Brush)FindResource("TextBrush"),
                            TextWrapping = TextWrapping.Wrap
                        }
                    });
                    code.Clear();
                    inCode = false;
                }
                else inCode = true;
                continue;
            }
            if (inCode) { code.Add(line); continue; }
            if (string.IsNullOrWhiteSpace(line))
            {
                panel.Children.Add(new Border { Height = 7, Background = Brushes.Transparent });
                continue;
            }
            var text = line.TrimStart();
            var heading = 0;
            while (heading < text.Length && heading < 3 && text[heading] == '#') heading++;
            if (heading > 0 && heading < text.Length && text[heading] == ' ')
            {
                var size = heading == 1 ? 25 : heading == 2 ? 21 : 18;
                panel.Children.Add(CreateMarkdownText(text[(heading + 1)..], size, FontWeights.SemiBold, new Thickness(0, 12, 0, 7)));
                continue;
            }
            if (text.StartsWith("> ", StringComparison.Ordinal))
            {
                panel.Children.Add(CreateMarkdownText(text[2..], 13, FontWeights.Normal, new Thickness(14, 2, 0, 8)));
                continue;
            }
            var bullet = text.StartsWith("- ", StringComparison.Ordinal) || text.StartsWith("* ", StringComparison.Ordinal);
            var numbered = Regex.IsMatch(text, @"^\d+\.\s+");
            if (bullet || numbered)
            {
                var content = bullet ? text[2..] : Regex.Replace(text, @"^\d+\.\s+", string.Empty);
                var prefix = bullet ? "• " : Regex.Match(text, @"^\d+\.").Value + " ";
                panel.Children.Add(CreateMarkdownText(prefix + content, 13.5, FontWeights.Normal, new Thickness(5, 2, 0, 5)));
                continue;
            }
            panel.Children.Add(CreateMarkdownText(line, 13.5, FontWeights.Normal, new Thickness(0, 0, 0, 8)));
        }
        if (inCode && code.Count > 0)
        {
            panel.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(17, 20, 24)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(13),
                Margin = new Thickness(0, 5, 0, 12),
                Child = new TextBlock
                {
                    Text = string.Join(Environment.NewLine, code),
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    FontSize = 12,
                    Foreground = (Brush)FindResource("TextBrush"),
                    TextWrapping = TextWrapping.Wrap
                }
            });
        }
        return panel;
    }



    private TextBlock CreateMarkdownText(string text, double size, FontWeight weight, Thickness margin)
    {
        var block = new TextBlock
        {
            FontSize = size,
            FontWeight = weight,
            Foreground = (Brush)FindResource("TextBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = margin
        };
        var pattern = new Regex(@"(\*\*.+?\*\*|`.+?`|\*.+?\*)");
        var index = 0;
        foreach (Match match in pattern.Matches(text))
        {
            if (match.Index > index) block.Inlines.Add(new Run(text[index..match.Index]));
            var value = match.Value;
            if (value.StartsWith("**", StringComparison.Ordinal) && value.EndsWith("**", StringComparison.Ordinal))
                block.Inlines.Add(new Bold(new Run(value[2..^2])));
            else if (value.StartsWith(new string('`', 1), StringComparison.Ordinal) && value.EndsWith(new string('`', 1), StringComparison.Ordinal))
                block.Inlines.Add(new Run(value[1..^1]) { FontFamily = new System.Windows.Media.FontFamily("Consolas") });
            else if (value.StartsWith("*", StringComparison.Ordinal) && value.EndsWith("*", StringComparison.Ordinal))
                block.Inlines.Add(new Italic(new Run(value[1..^1])));
            index = match.Index + match.Length;
        }
        if (index < text.Length) block.Inlines.Add(new Run(text[index..]));
        return block;
    }



    private async void AiAsk_Click(object sender, RoutedEventArgs e)
    {
        var question = AiInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(question)) return;
        AddAiMessage("You", question);
        AiInput.Clear(); AiStatusText.Text = "Thinking…";
        try
        {
            var payload = new { message = question, power = _aiPower, attachments = string.IsNullOrWhiteSpace(AiAttachment.Text) || AiAttachment.Text == "No attachment" ? Array.Empty<object>() : new[] { new { name = AiAttachment.Text, content = "Attachment selected in Airly Client" } } };
            using var response = await _http.PostAsJsonAsync($"{ClientConfig.ApiBaseUrl}api/ai/helper", payload);
            var result = await response.Content.ReadFromJsonAsync<AiHelperResponse>();
            AddAiMessage("Airly Helper", result?.reply ?? "The aviation helper service did not return a response.");
            AiStatusText.Text = response.IsSuccessStatusCode ? "Ready" : "Service unavailable";
            await RenameActiveChatAsync(question);
        }
        catch (Exception ex)
        {
            AddAiMessage("Airly Helper", $"I could not reach the helper service: {ex.Message}");
            AiStatusText.Text = "Offline";
            await RenameActiveChatAsync(question);
        }
        finally { AiInput.Focus(); }
    }

    private async Task RenameActiveChatAsync(string question)
    {
        if (_aiTitleRequestRunning || _activeAiChat is null || _activeAiChat.Messages.Count == 0) return;
        _aiTitleRequestRunning = true;
        try
        {
            var titlePayload = new { message = "Create a concise 3-6 word title for this aviation chat. Return ONLY the title, no punctuation, quotes, markdown or explanation. User question: " + question, power = "Minimal", attachments = Array.Empty<object>() };
            using var response = await _http.PostAsJsonAsync($"{ClientConfig.ApiBaseUrl}api/ai/helper", titlePayload);
            var result = await response.Content.ReadFromJsonAsync<AiHelperResponse>();
            var title = CleanChatTitle(result?.reply);
            _activeAiChat.Title = string.IsNullOrWhiteSpace(title) ? CreateFallbackChatTitle(question) : title;
        }
        catch { _activeAiChat.Title = CreateFallbackChatTitle(question); }
        finally
        {
            AiChatList.Items.Refresh(); SaveAiChats(); _aiTitleRequestRunning = false;
        }
    }

    private static string CleanChatTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var title = value.Replace("\r", " ").Replace("\n", " ").Trim().Trim('"', '\'', '*', '#', ':', '-');
        title = Regex.Replace(title, @"\s+", " ");
        return title.Length > 42 ? title[..42].TrimEnd() : title;
    }

    private static string CreateFallbackChatTitle(string question)
    {
        var cleaned = Regex.Replace(question.Trim(), @"\s+", " ");
        if (cleaned.Length <= 38) return cleaned;
        var cut = cleaned[..38]; var lastSpace = cut.LastIndexOf(' ');
        return (lastSpace > 12 ? cut[..lastSpace] : cut).TrimEnd() + "…";
    }

    private void AiFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = false, Filter = "Aviation data|*.txt;*.csv;*.json;*.xml;*.log|All files|*.*" };
        if (dialog.ShowDialog() == true) { AiAttachment.Text = System.IO.Path.GetFileName(dialog.FileName); }
    }

    private string _aiPower = "Medium";

    private void AiPower_Click(object sender, RoutedEventArgs e)
    {
        AiPowerBox.Focus();
        AiPowerBox.IsDropDownOpen = true;
    }

    private void AiPower_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || AiPowerBox.SelectedItem is not ComboBoxItem item) return;
        var value = item.Content?.ToString() ?? "Medium";
        _aiPower = value.StartsWith("Fast", StringComparison.OrdinalIgnoreCase) ? "Minimal" : value.StartsWith("Extra", StringComparison.OrdinalIgnoreCase) ? "Extra" : "Medium";
    }



    private void AiInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            AiAsk_Click(sender, new RoutedEventArgs());
        }
    }

    private void Window_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (AiHelperView.Visibility != Visibility.Visible || AiInput.IsKeyboardFocusWithin) return;
        if (Keyboard.FocusedElement is TextBox || Keyboard.FocusedElement is ComboBoxItem || Keyboard.FocusedElement is Button) return;
        AiInput.Focus();
        AiInput.CaretIndex = AiInput.Text.Length;
        AiInput.AppendText(e.Text);
        e.Handled = true;
    }

    private void InitializeEmojiPicker()
    {
        var emojis = new[]
        {
            "😀","😃","😄","😁","😆","😅","😂","🤣","😊","😇","🙂","🙃","😉","😌","😍","🥰","😘","😎","🤓","🫡",
            "🤔","🤨","😐","😑","😶","🙄","😏","😴","🤯","😮","😲","😳","🥳","🤩","😬","😢","😭","😡",
            "👍","👎","👏","🙌","👌","✌️","🤝","🙏","💪","👀","🫶","❤️","🧡","💛","💚","💙","💜","🖤","🤍","🤎",
            "🔥","⭐","✨","💯","⚡","✈️","🛫","🛬","🛩️","🚁","🎧","🎙️","📡","🗺️","🧭","⛅","☀️","🌧️","❄️",
            "🇮🇪","🇬🇧","🇺🇸","🇵🇹","🇪🇸","🇫🇷","🇩🇪","🇧🇷","🇨🇦","🇦🇺","🇯🇵","🇳🇱","🇨🇭","🇳🇴","🇮🇸","🇦🇪"
        };
        foreach (var emoji in emojis)
        {
            var button = new Button
            {
                Content = emoji, Width = 42, Height = 38, FontSize = 19, Margin = new Thickness(2),
                Style = (Style)FindResource("SecondaryButton"), BorderThickness = new Thickness(0), Tag = emoji
            };
            button.Click += AiEmojiInsert_Click;
            AiEmojiWrap.Children.Add(button);
        }
    }

    private void AiEmoji_Click(object sender, RoutedEventArgs e)
    {
        var open = AiEmojiPanel.Visibility == Visibility.Visible;
        if (open)
        {
            AiEmojiTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, 28, TimeSpan.FromMilliseconds(130)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
            AiEmojiPanel.Visibility = Visibility.Collapsed;
            return;
        }
        AiEmojiPanel.Visibility = Visibility.Visible;
        AiEmojiTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(28, 0, TimeSpan.FromMilliseconds(170)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private void AiEmojiInsert_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string emoji)
        {
            AiInput.SelectedText = emoji;
            AiInput.CaretIndex += emoji.Length;
            AiInput.Focus();
            AiEmojiPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void RenderColorWheel()
    {
        const int size = 180;
        var pixels = new byte[size * size * 4];
        var center = size / 2.0;
        var radius = size / 2.0 - 1;
        for (var y = 0; y < size; y++)
        for (var xx = 0; xx < size; xx++)
        {
            var dx = (xx - center) / radius;
            var dy = (y - center) / radius;
            var distance = Math.Sqrt(dx * dx + dy * dy);
            var index = (y * size + xx) * 4;
            if (distance > 1) { pixels[index + 3] = 0; continue; }
            var hue = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360;
            var color = HsvToColor(hue, Math.Min(1, distance), 1);
            pixels[index] = color.B; pixels[index + 1] = color.G; pixels[index + 2] = color.R; pixels[index + 3] = 255;
        }
        var bitmap = new WriteableBitmap(size, size, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, size, size), pixels, size * 4, 0);
        CustomColorWheel.Source = bitmap;
    }

    private void CustomColorWheel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(CustomColorWheel);
        var center = new Point(CustomColorWheel.ActualWidth / 2, CustomColorWheel.ActualHeight / 2);
        var dx = p.X - center.X; var dy = p.Y - center.Y;
        var radius = Math.Max(1, Math.Min(center.X, center.Y));
        var distance = Math.Min(1, Math.Sqrt(dx * dx + dy * dy) / radius);
        var hue = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360;
        var color = HsvToColor(hue, distance, 1);
        _pendingAccentColor = color.ToString();
        CustomColorHexBox.Text = _pendingAccentColor;
        CustomColorPreview.Background = new SolidColorBrush(color);
    }

    private void CustomColorApply_Click(object sender, RoutedEventArgs e)
    {
        var value = CustomColorHexBox.Text.Trim();
        if (Regex.IsMatch(value, "^#[0-9A-Fa-f]{6}$"))
        {
            _pendingAccentColor = value;
            CustomColorPreview.Background = new SolidColorBrush(ParseColor(value, Colors.Gray));
        }
    }

    private static Color HsvToColor(double h, double s, double v)
    {
        var c = v * s; var x = c * (1 - Math.Abs((h / 60 % 2) - 1)); var m = v - c;
        double r=0,g=0,b=0;
        if (h < 60) { r=c; g=x; } else if (h < 120) { r=x; g=c; } else if (h < 180) { g=c; b=x; }
        else if (h < 240) { g=x; b=c; } else if (h < 300) { r=x; b=c; } else { r=c; b=x; }
        return Color.FromRgb((byte)Math.Round((r+m)*255), (byte)Math.Round((g+m)*255), (byte)Math.Round((b+m)*255));
    }

    private void InitializeAiChats()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_aiChatFilePath)!);
            if (File.Exists(_aiChatFilePath))
            {
                var json = File.ReadAllText(_aiChatFilePath);
                var loaded = JsonSerializer.Deserialize<List<AiChatSession>>(json);
                if (loaded != null)
                    foreach (var chat in loaded.Where(chat => chat.Messages.Count > 0))
                        _aiChats.Add(chat);
            }
        }
        catch { }

        _activeAiChat = _aiChats.LastOrDefault();
        if (_activeAiChat == null)
        {
            _activeAiChat = new AiChatSession { Title = "New chat" };
            _aiChats.Add(_activeAiChat);
        }
        AiChatList.ItemsSource = _aiChats;
        AiChatList.SelectedItem = _activeAiChat;
        RenderActiveAiChat();
    }

    private void EnsureActiveAiChat()
    {
        if (_activeAiChat != null) return;
        if (_aiChats.Count == 0)
            _aiChats.Add(new AiChatSession { Title = "New chat" });
        _activeAiChat = _aiChats[_aiChats.Count - 1];
    }

    private void SaveAiChats()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_aiChatFilePath)!);
            var saved = _aiChats.Where(chat => chat.Messages.Count > 0).ToList();
            File.WriteAllText(_aiChatFilePath, JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private void RenderActiveAiChat()
    {
        if (AiMessages == null) return;
        EnsureActiveAiChat();
        AiMessages.Children.Clear();
        foreach (var message in _activeAiChat!.Messages)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 22) };
            var bubble = new Border
            {
                Background = message.Sender == "You" ? (Brush)FindResource("InputBrush") : Brushes.Transparent,
                BorderBrush = message.Sender == "You" ? (Brush)FindResource("LineBrush") : Brushes.Transparent,
                BorderThickness = message.Sender == "You" ? new Thickness(1) : new Thickness(0),
                CornerRadius = new CornerRadius(16),
                Padding = message.Sender == "You" ? new Thickness(15, 11, 15, 11) : new Thickness(0),
                MaxWidth = 900,
                HorizontalAlignment = message.Sender == "You" ? HorizontalAlignment.Right : HorizontalAlignment.Left
            };
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = message.Sender == "You" ? "You" : "Airly AI",
                FontSize = 10, FontWeight = FontWeights.SemiBold,
                Foreground = message.Sender == "You" ? (Brush)FindResource("MutedBrush") : (Brush)FindResource("AccentBrush"),
                Margin = new Thickness(0, 0, 0, 6)
            });
            if (message.Sender == "You")
                stack.Children.Add(new TextBlock { Text = message.Text, FontSize = 13.5, Foreground = (Brush)FindResource("TextBrush"), TextWrapping = TextWrapping.Wrap });
            else
                stack.Children.Add(RenderAiMarkdown(message.Text));
            bubble.Child = stack; row.Children.Add(bubble); AiMessages.Children.Add(row);
        }
        AiScroll.ScrollToEnd();
    }

    private void AiHistoryToggle_Click(object sender, RoutedEventArgs e)
    {
        var collapsed = AiHistoryColumn.Width.Value > 0;
        AiHistoryColumn.Width = collapsed ? new GridLength(0) : new GridLength(250);
        AiHistoryToggle.Content = collapsed ? "›" : "‹";
        AiHistoryToggle.ToolTip = collapsed ? "Show chat history" : "Hide chat history";
    }

    private void AiNewChat_Click(object sender, RoutedEventArgs e)
    {
        if (_activeAiChat is not null && _activeAiChat.Messages.Count == 0)
            _aiChats.Remove(_activeAiChat);
        var chat = new AiChatSession { Title = "New chat" };
        _aiChats.Add(chat); _activeAiChat = chat;
        AiChatList.ItemsSource = null; AiChatList.ItemsSource = _aiChats;
        AiChatList.SelectedItem = chat; RenderActiveAiChat(); AiInput.Focus();
    }

    private void AiChatList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AiChatList.SelectedItem is AiChatSession chat) { _activeAiChat = chat; RenderActiveAiChat(); }
    }

    private sealed class AiChatSession { public string Title { get; set; } = "New chat"; public List<AiChatMessage> Messages { get; set; } = new(); }
    private sealed class AiChatMessage { public string Sender { get; set; } = "Airly AI"; public string Text { get; set; } = string.Empty; }

    private void LoadSettingsIntoUi()
    {
        SimBriefPilotIdBox.Text = _settings.SimBriefPilotId; AirlyIdBox.Text = _settings.AirlyId; UsernameBox.Text = _settings.Username;
        SettingsAirlyIdBox.Text = _settings.AirlyId; SettingsUsernameBox.Text = _settings.Username;
        StartWithWindowsBox.IsChecked = _settings.StartWithWindows; AutoConnectBox.IsChecked = _settings.AutoConnect;
        EnableAtcAudioBox.IsChecked = _settings.EnableAtcAudio; EnableMultiplayerBox.IsChecked = _settings.EnableMultiplayer; AutomaticModelMatchingBox.IsChecked = _settings.AutomaticModelMatching;
        ReduceAnimationsBox.IsChecked = _settings.ReduceAnimations; LimitFpsBox.IsChecked = _settings.LimitFps; LowBandwidthBox.IsChecked = _settings.LowBandwidth;
        HardwareAccelerationBox.IsChecked = _settings.HardwareAcceleration; CacheMapTilesBox.IsChecked = _settings.CacheMapTiles; CompactTrafficBox.IsChecked = _settings.CompactTraffic;
        EnableSoundEffectsBox.IsChecked = _settings.EnableSoundEffects; PushToTalkBox.IsChecked = _settings.PushToTalk; VoiceVolumeSlider.Value = _settings.VoiceVolume;
        UiScaleBox.SelectedItem = UiScaleBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Content?.ToString() == _settings.UiScalePercent + "%") ?? UiScaleBox.Items[0];
        NetworkUpdateRateBox.SelectedItem = NetworkUpdateRateBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Content?.ToString() == _settings.NetworkUpdateRate) ?? NetworkUpdateRateBox.Items[0];
        AutomaticUpdatesBox.IsChecked = _settings.AutomaticUpdates; ReleaseNotesBox.IsChecked = _settings.ShowReleaseNotes;
        _pendingThemeMode = string.Equals(_settings.ThemeMode, "Bright", StringComparison.OrdinalIgnoreCase) ? "Bright" : "Dark";
        _pendingAccentColor = Regex.IsMatch(_settings.AccentColor ?? "", "^#[0-9A-Fa-f]{6}$") ? _settings.AccentColor : GetDefaultAccent(_pendingThemeMode);
        PopulatePaletteButtons(); UpdateThemeSelectionVisuals(); CustomColorHexBox.Text = _pendingAccentColor;
        CustomColorPreview.Background = new SolidColorBrush(ParseColor(_pendingAccentColor, Colors.Gray));
    }

    private static readonly (string Name, string Hex)[] Palette =
    {
        ("Slate","#80858B"),("Ocean","#1689B8"),("Aurora","#27B8A4"),("Violet","#8B5CF6"),("Rose","#E05272"),
        ("Amber","#D99119"),("Emerald","#22A06B"),("Glacier","#53B6D6"),("Coral","#E56A4A"),("Sky","#3487D7"),
        ("Indigo","#5865D9"),("Mint","#45B78B"),("Gold","#C69B32"),("Ruby","#D63D58"),("Plum","#9B59B6")
    };

    private void InitializeTheme()
    {
        _pendingThemeMode = string.Equals(_settings.ThemeMode, "Bright", StringComparison.OrdinalIgnoreCase) ? "Bright" : "Dark";
        _pendingAccentColor = Regex.IsMatch(_settings.AccentColor ?? "", "^#[0-9A-Fa-f]{6}$") ? _settings.AccentColor : GetDefaultAccent(_pendingThemeMode);
        ApplyTheme(); PopulatePaletteButtons(); UpdateThemeSelectionVisuals();
    }

    private void ThemeDark_Click(object sender, RoutedEventArgs e) { _pendingThemeMode = "Dark"; UpdateThemeSelectionVisuals(); }
    private void ThemeBright_Click(object sender, RoutedEventArgs e) { _pendingThemeMode = "Bright"; UpdateThemeSelectionVisuals(); }

    private void PaletteButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string hex && Regex.IsMatch(hex, "^#[0-9A-Fa-f]{6}$"))
        {
            _pendingAccentColor = hex; CustomColorHexBox.Text = hex; CustomColorPreview.Background = new SolidColorBrush(ParseColor(hex, Colors.Gray));
        }
    }

    private void PopulatePaletteButtons()
    {
        if (PaletteWrap == null) return;
        PaletteWrap.Children.Clear();
        foreach (var palette in Palette)
        {
            var button = new Button { Tag = palette.Hex, Width = 118, Height = 56, Margin = new Thickness(0,0,8,8), Padding = new Thickness(6), Style = (Style)FindResource("SecondaryButton"), BorderThickness = new Thickness(1) };
            var stack = new StackPanel();
            stack.Children.Add(new Border { Height = 20, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(ParseColor(palette.Hex, Colors.Gray)) });
            stack.Children.Add(new TextBlock { Text = palette.Name, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0,4,0,0) });
            button.Content = stack; button.Click += PaletteButton_Click; PaletteWrap.Children.Add(button);
        }
    }

    private void UpdateThemeSelectionVisuals() { ThemeDarkButton.Opacity = _pendingThemeMode == "Dark" ? 1.0 : 0.55; ThemeBrightButton.Opacity = _pendingThemeMode == "Bright" ? 1.0 : 0.55; }
    private static string GetDefaultAccent(string mode) => Palette[0].Hex;

    private void ApplyTheme()
    {
        var bright = string.Equals(_settings.ThemeMode, "Bright", StringComparison.OrdinalIgnoreCase);
        var accent = ParseColor(_settings.AccentColor, ParseColor(GetDefaultAccent(_settings.ThemeMode), Colors.Gray));
        var window = bright ? "#F3F5F6" : "#0A0D11"; var sidebar = bright ? "#FFFFFF" : "#080B0F"; var glass = bright ? "#FFFFFF" : "#12171D";
        var panel = bright ? "#FAFBFC" : "#151B22"; var input = bright ? "#F6F8F9" : "#0E1318"; var line = bright ? "#D8E0E4" : "#27303A";
        var text = bright ? "#17232C" : "#F2F5F7"; var muted = bright ? "#62717B" : "#9BA7B1"; var accentSoft = Color.FromArgb(bright ? (byte)28 : (byte)48, accent.R, accent.G, accent.B);
        SetBrush("WindowBrush", window); SetBrush("SidebarBrush", sidebar); SetBrush("GlassBrush", glass); SetBrush("PanelBrush", panel); SetBrush("InputBrush", input); SetBrush("LineBrush", line);
        SetBrush("TextBrush", text); SetBrush("MutedBrush", muted); SetBrush("AccentBrush", accent); SetBrush("AccentSoftBrush", accentSoft);
        SetBrush("SuccessBrush", bright ? "#167447" : "#62C995"); SetBrush("DangerBrush", bright ? "#B42318" : "#E68181");
        Background = (Brush)Resources["WindowBrush"]; Foreground = (Brush)Resources["TextBrush"];
    }







    private void LoadDemoUiState()
    {
        FrequencyGrid.ItemsSource = Array.Empty<object>();
        ChartList.ItemsSource = Array.Empty<object>();
    }

    private async void LoadFrequencies_Click(object sender, RoutedEventArgs e)
    {
        var icao = FrequencyAirportBox.Text.Trim().ToUpperInvariant();
        if (icao.Length != 4) { FrequencyStatus.Text = "Enter a four-letter ICAO code."; return; }
        FrequencyStatus.Text = "Loading worldwide airport frequency data...";
        try
        {
            var frequencies = await _airportData.GetFrequenciesAsync(icao);
            FrequencyGrid.ItemsSource = frequencies.Select(f => new
            {
                Airport = icao,
                Position = f.Type ?? "Other",
                Frequency = f.FrequencyMHz.ToString("0.000"),
                Controller = f.Description ?? "—"
            }).ToList();
            FrequencyStatus.Text = frequencies.Count == 0 ? "No source frequencies are available for this airport." : $"{frequencies.Count} source frequencies loaded.";
        }
        catch (Exception ex) { FrequencyStatus.Text = $"Frequency service unavailable: {ex.Message}"; }
    }

    private async void LoadCharts_Click(object sender, RoutedEventArgs e)
    {
        var icao = ChartAirportBox.Text.Trim().ToUpperInvariant();
        if (icao.Length != 4) { ChartStatus.Text = "Enter a four-letter ICAO code."; return; }
        ChartStatus.Text = "Loading verified chart sources...";
        try
        {
            var result = await _airportData.GetChartsAsync(icao);
            ChartList.ItemsSource = result?.Charts ?? new List<AirportDataService.ChartSource>();
            ChartStatus.Text = result?.Note ?? "No verified chart source returned.";
            if (result?.Charts is { Count: > 0 })
                ChartStatus.Text += $"  {result.Charts.Count} provider source(s) available.";
        }
        catch (Exception ex) { ChartStatus.Text = $"Chart service unavailable: {ex.Message}"; }
    }

    private void ChartOpen_Click(object sender, RoutedEventArgs e)
    {
        var url = (sender as Button)?.Tag?.ToString();
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        var name = (sender as Button)?.Tag?.ToString() ?? "Dashboard";
        var views = new Dictionary<string, UIElement>
        {
            ["Dashboard"] = DashboardView, ["Connect"] = ConnectView, ["AiHelper"] = AiHelperView,
            ["Tracking"] = TrackingView, ["Frequencies"] = FrequenciesView, ["Weather"] = WeatherView,
            ["Charts"] = ChartsView, ["FlightPlan"] = FlightPlanView, ["Models"] = ModelsView, ["Settings"] = SettingsView
        };

        foreach (var view in views.Values)
            view.Visibility = Visibility.Collapsed;

        if (!views.TryGetValue(name, out var selected))
            return;

        selected.Visibility = Visibility.Visible;
        selected.Opacity = 0;
        selected.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });
        PageTitle.Text = name switch
        {
            "Dashboard" => "Overview", "Tracking" => "Track Flights", "FlightPlan" => "Flight Plan",
            "Frequencies" => "ATC Frequencies", "Weather" => "Weather / METAR", "Charts" => "Charts",
            "Models" => "Model Matching", "AiHelper" => "Airly AI", _ => name
        };
        PageEyebrow.Text = name switch
        {
            "Tracking" => "LIVE FLIGHT TRACKING", "FlightPlan" => "DISPATCH", "Frequencies" => "AIR TRAFFIC CONTROL",
            "Weather" => "FLIGHT OPERATIONS", "Charts" => "NAVIGATION", "Settings" => "CLIENT CONFIGURATION",
            _ => "AIRLY NETWORK"
        };
        PageSubtitle.Text = name switch
        {
            "Tracking" => "Live aircraft positions on a satellite map",
            "FlightPlan" => "Import the latest operational flight plan from SimBrief",
            "Frequencies" => "Controller positions and authenticated frequency audio",
            "Weather" => "Airport weather and METAR information",
            "Charts" => "Airport and instrument procedure charts",
            "AiHelper" => "A focused assistant for aviation questions and calculations",
            _ => "Flight simulation network operations"
        };
        if (name == "AiHelper") Dispatcher.BeginInvoke(() => AiInput.Focus());
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_connected) { Disconnect(); return; }
        ConnectButton.IsEnabled = false;
        ConnectStatus.Text = "Validating Airly membership…";

        try
        {
            var id = _settings.AirlyId.Trim();
            var region = ((ComboBoxItem)RegionBox.SelectedItem)?.Content?.ToString() ?? "Europe";
            if (string.IsNullOrWhiteSpace(id)) { ConnectStatus.Text = "Enter your Airly ID."; return; }

            var response = await _http.PostAsJsonAsync($"{ClientConfig.ApiBaseUrl}api/client/activate", new { region, airlyId=id });
            var result = await response.Content.ReadFromJsonAsync<ActivationResponse>();

            if (!response.IsSuccessStatusCode || result is null || !result.Valid)
            {
                ConnectStatus.Text = result?.Message ?? "Activation service unavailable.";
                return;
            }

            _connected = true;
            SetConnectionState(true);
            ConnectStatus.Text = "Authenticated. Realtime network session is ready.";
        }
        catch (Exception ex) { ConnectStatus.Text = $"Connection failed: {ex.Message}"; }
        finally { ConnectButton.IsEnabled = true; }
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e) => Disconnect();

    private void Disconnect()
    {
        _connected = false;
        SetConnectionState(false);
        ConnectStatus.Text = "Disconnected from Airly.";
    }

    private void SetConnectionState(bool connected)
    {
        ConnectionLabel.Text = connected ? "CONNECTED" : "DISCONNECTED";
        ConnectionLabel.Foreground = connected ? System.Windows.Media.Brushes.LightGreen : System.Windows.Media.Brushes.LightPink;
        TopStatusText.Text = connected ? "ONLINE" : "OFFLINE";
        TopStatusText.Foreground = connected ? System.Windows.Media.Brushes.LightGreen : System.Windows.Media.Brushes.LightPink;
        NetworkStatus.Text = connected ? "Online" : "Offline";
        OwnAircraft.Text = connected ? "Ready" : "Offline";
        ConnectButton.Content = connected ? "Disconnect" : "Connect";
        TrafficCount.Text = _liveAircraft.Count.ToString();
        ActivityText.Text = connected
            ? "Connected. Traffic, ATC, voice and simulator state can synchronize through the Airly realtime service."
            : "Connect to Airly to receive live traffic, ATC and network data.";
    }

    private async void ImportSimBrief_Click(object sender, RoutedEventArgs e)
    {
        var pilotId = _settings.SimBriefPilotId.Trim();

        if (string.IsNullOrWhiteSpace(pilotId))
        {
            FlightPlanStatus.Text = "Add your SimBrief Pilot ID in Settings first.";
            Nav_Click(new Button { Tag = "Settings" }, new RoutedEventArgs());
            return;
        }

        FlightPlanStatus.Text = "Fetching your latest SimBrief OFP…";

        try
        {
            var url = $"https://www.simbrief.com/api/xml.fetcher.php?userid={Uri.EscapeDataString(pilotId)}&json=v2";
            using var response = await _http.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                FlightPlanStatus.Text = $"SimBrief returned HTTP {(int)response.StatusCode}. Generate a flight plan in SimBrief and try again.";
                return;
            }

            var json = await response.Content.ReadAsStringAsync();
            _flightPlan = SimBriefFlightPlan.FromJson(json, pilotId);
            ApplyFlightPlan(_flightPlan);
            FlightPlanStatus.Text = $"Imported {_flightPlan.Callsign} from SimBrief. This import only occurs when you press the button.";
            RefreshTracking();
        }
        catch (JsonException)
        {
            FlightPlanStatus.Text = "SimBrief returned data that Airly could not parse.";
        }
        catch (Exception ex)
        {
            FlightPlanStatus.Text = $"SimBrief import failed: {ex.Message}";
        }
    }

    private void ApplyFlightPlan(SimBriefFlightPlan plan)
    {
        PlanCallsign.Text = plan.Callsign;
        PlanRoute.Text = $"{plan.Origin} → {plan.Destination}";
        PlanCruise.Text = FormatAltitude(plan.CruiseAltitudeFeet);
        PlanAirports.Text = $"{plan.Origin} → {plan.Destination}";
        PlanAircraft.Text = string.IsNullOrWhiteSpace(plan.Aircraft) ? "—" : plan.Aircraft;
        PlanRegistration.Text = string.IsNullOrWhiteSpace(plan.Registration) ? "—" : plan.Registration;
        PlanAlternate.Text = string.IsNullOrWhiteSpace(plan.Alternate) ? "—" : plan.Alternate;
        PlanTimes.Text = $"{plan.DepartureTime} → {plan.ArrivalTime}";
        PlanAirac.Text = string.IsNullOrWhiteSpace(plan.Airac) ? "—" : plan.Airac;
        PlanFullRoute.Text = plan.RouteSummary;
        DashboardRoute.Text = $"{plan.Origin} → {plan.Destination}";
        DashboardCruise.Text = FormatAltitude(plan.CruiseAltitudeFeet);
        DashboardCallsign.Text = plan.Callsign;
    }

    private void RefreshTracking()
    {
        _trackedFlights.Clear();
        foreach (var aircraft in _liveAircraft)
        {
            var planMatches = _flightPlan is not null && string.Equals(aircraft.Callsign, _flightPlan.Callsign, StringComparison.OrdinalIgnoreCase);
            _trackedFlights.Add(new TrackedFlight(
                aircraft.Callsign ?? aircraft.Icao24, aircraft.Aircraft,
                planMatches ? _flightPlan!.Registration : string.Empty,
                planMatches ? _flightPlan!.Origin : "—",
                planMatches ? _flightPlan!.Destination : "—",
                planMatches ? _flightPlan!.RouteSummary : "—",
                aircraft.Latitude!.Value, aircraft.Longitude!.Value,
                aircraft.AltitudeFeet ?? 0, aircraft.GroundSpeedKnots,
                aircraft.HeadingDegrees, aircraft.VerticalSpeedFeetPerMinute,
                planMatches ? _flightPlan!.CruiseAltitudeFeet : 0,
                "—",
                aircraft.VerticalSpeedFeetPerMinute > 300 ? "CLIMB" : aircraft.VerticalSpeedFeetPerMinute < -300 ? "DESCENT" : aircraft.OnGround ? "GROUND" : "CRUISE",
                string.Empty, BuildJetPhotosSearchUrl(planMatches ? _flightPlan!.Registration : (aircraft.Callsign ?? aircraft.Icao24)),
                aircraft.Country, aircraft.OnGround, aircraft.Icao24, aircraft.LastContact));
        }
        TrackingGrid.ItemsSource = _trackedFlights;
        TrafficCount.Text = _liveAircraft.Count.ToString();
        _ = UpdateTrackingMapAsync();
    }

    private async Task RefreshLiveTrackingAsync()
    {
        if (_trackingRequestRunning) return;
        _trackingRequestRunning = true;
        try
        {
            using var response = await _http.GetAsync(ClientConfig.ApiBaseUrl + "api/tracker", HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                ActivityText.Text = "Live tracker unavailable (HTTP " + (int)response.StatusCode + ").";
                return;
            }
            var payload = await response.Content.ReadFromJsonAsync<TrackerResponse>();
            _liveAircraft.Clear();
            if (payload?.Aircraft is not null)
                _liveAircraft.AddRange(payload.Aircraft.Where(a => a.Latitude is not null && a.Longitude is not null));
            RefreshTracking();
            try
            {
                var network = await _http.GetFromJsonAsync<NetworkStatusResponse>(ClientConfig.ApiBaseUrl + "api/network/status");
                ControllerCount.Text = (network?.Controllers ?? 0).ToString();
                ActivityText.Text = "Live tracker: " + _liveAircraft.Count + " aircraft. Controller positions: " + (network?.Controllers ?? 0) + ".";
            }
            catch { ActivityText.Text = "Live tracker: " + _liveAircraft.Count + " aircraft. Network controller status unavailable."; }
        }
        catch (Exception ex) { ActivityText.Text = "Live tracker unavailable: " + ex.Message; }
        finally { _trackingRequestRunning = false; }
    }

    private async Task InitializeTrackingMapAsync()
    {
        try
        {
            await TrackingMap.EnsureCoreWebView2Async();
            TrackingMap.NavigateToString("""
<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<link rel="stylesheet" href="https://unpkg.com/leaflet@1.9.4/dist/leaflet.css">
<style>html,body,#map{height:100%;width:100%;margin:0;background:#10151a;overflow:hidden}.leaflet-control-zoom a{background:#151b22!important;color:#f2f5f7!important;border-color:#303943!important}.leaflet-control-attribution{background:rgba(8,11,15,.82)!important;color:#d2d7dc!important}.leaflet-control-attribution a{color:#b8c8d8}.plane{font-size:23px;line-height:28px;width:28px;height:28px;text-align:center;color:#fff;text-shadow:0 1px 4px #000;transform-origin:center}</style>
</head><body><div id="map"></div><script src="https://unpkg.com/leaflet@1.9.4/dist/leaflet.js"></script><script>
const map=L.map('map',{zoomControl:true,preferCanvas:true}).setView([53.35,-6.26],6);
L.tileLayer('https://services.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}',{maxZoom:19,attribution:'Esri, Maxar, Earthstar Geographics, and the GIS User Community'}).addTo(map);
const markers=new Map();
function planeIcon(heading){return L.divIcon({className:'airly-plane',html:'<div class="plane" style="transform:rotate('+Number(heading||0)+'deg)">✈</div>',iconSize:[28,28],iconAnchor:[14,14]});}
window.airlyUpdate=function(data){const seen=new Set();(data||[]).forEach(a=>{if(a.latitude==null||a.longitude==null)return;const key=a.icao24||a.callsign||Math.random().toString();seen.add(key);let m=markers.get(key);const pos=[Number(a.latitude),Number(a.longitude)];if(!m){m=L.marker(pos,{icon:planeIcon(a.headingDegrees)}).addTo(map);markers.set(key,m)}else{m.setLatLng(pos);const el=m.getElement();const plane=el&&el.querySelector('.plane');if(plane)plane.style.transform='rotate('+Number(a.headingDegrees||0)+'deg)'}const call=(a.callsign||'UNKNOWN').trim()||'UNKNOWN';const alt=a.altitudeFeet==null?'—':Math.round(a.altitudeFeet).toLocaleString()+' ft';const spd=a.groundSpeedKnots==null?'—':Math.round(a.groundSpeedKnots)+' kt';m.bindTooltip(call+' · '+alt+' · '+spd,{direction:'top',offset:[0,-12]});m.bindPopup('<b>'+call+'</b><br>'+alt+'<br>'+spd)});markers.forEach((m,key)=>{if(!seen.has(key)){map.removeLayer(m);markers.delete(key)}})};
window.airlyFocus=function(callsign){const target=String(callsign||'').trim().toUpperCase();markers.forEach(m=>{const tip=m.getTooltip();if(tip&&tip.getContent().toUpperCase().startsWith(target+' ·')){map.setView(m.getLatLng(),Math.max(map.getZoom(),8),{animate:true});m.openPopup()}})};
window.addEventListener('resize',()=>map.invalidateSize());
</script></body></html>
""");
            await UpdateTrackingMapAsync();
        }
        catch (Exception ex) { ActivityText.Text = "Satellite map unavailable: " + ex.Message; }
    }

    private async Task UpdateTrackingMapAsync()
    {
        if (TrackingMap.CoreWebView2 is null) return;
        await TrackingMap.ExecuteScriptAsync("window.airlyUpdate(" + JsonSerializer.Serialize(_liveAircraft) + ");");
    }

    private async Task FocusTrackingMapAsync(string callsign)
    {
        if (TrackingMap.CoreWebView2 is null) return;
        await TrackingMap.ExecuteScriptAsync("window.airlyFocus(" + JsonSerializer.Serialize(callsign) + ");");
    }

    private void TrackingSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = TrackingSearchBox.Text.Trim();
        TrackingGrid.ItemsSource = string.IsNullOrWhiteSpace(query)
            ? _trackedFlights
            : _trackedFlights.Where(f =>
                f.Callsign.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                f.Aircraft.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                f.Origin.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                f.Destination.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void ClearTracking_Click(object sender, RoutedEventArgs e)
    {
        TrackingSearchBox.Clear();
        TrackingGrid.ItemsSource = _trackedFlights;
    }

    private void TrackingGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedFlight = TrackingGrid.SelectedItem as TrackedFlight;
        if (_selectedFlight is null)
        {
            TrackCallsign.Text = "Select a flight";
            TrackAircraft.Text = "—";
            JetPhotosButton.Visibility = Visibility.Collapsed;
            return;
        }

        TrackCallsign.Text = _selectedFlight.Callsign;
        TrackAircraft.Text = string.IsNullOrWhiteSpace(_selectedFlight.Registration)
            ? _selectedFlight.Aircraft + " • " + _selectedFlight.Country
            : _selectedFlight.Aircraft + " • " + _selectedFlight.Registration + " • " + _selectedFlight.Country;
        _ = FocusTrackingMapAsync(_selectedFlight.Callsign);
        TrackRoute.Text = $"{_selectedFlight.Origin} → {_selectedFlight.Destination}";
        TrackPosition.Text = $"{_selectedFlight.Latitude:F4}, {_selectedFlight.Longitude:F4}";
        TrackHeading.Text = $"{_selectedFlight.HeadingDegrees:000}°";
        TrackAltitude.Text = FormatAltitude(_selectedFlight.AltitudeFeet);
        TrackCruise.Text = _selectedFlight.CruiseAltitudeFeet > 0 ? FormatAltitude(_selectedFlight.CruiseAltitudeFeet) : "Not in plan";
        TrackSpeed.Text = $"{_selectedFlight.GroundSpeedKnots:N0} kt";
        PhotoStatus.Text = string.IsNullOrWhiteSpace(_selectedFlight.PhotoUrl)
            ? "JetPhotos search is available for this aircraft."
            : "Photo source: JetPhotos";
        JetPhotosButton.Tag = _selectedFlight.PhotoSourceUrl;
        JetPhotosButton.Visibility = Visibility.Visible;
    }

    private void JetPhotos_Click(object sender, RoutedEventArgs e)
    {
        var url = (sender as Button)?.Tag?.ToString();
        if (!string.IsNullOrWhiteSpace(url))
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private async void OverviewAirportSearch_Click(object sender, RoutedEventArgs e)
    {
        var query = OverviewAirportBox.Text.Trim();
        if (query.Length < 2) { OverviewAirportName.Text = "Enter an airport name, ICAO or IATA code."; return; }
        try
        {
            var matches = await _airportData.SearchAirportsAsync(query);
            OverviewAirportBox.ItemsSource = matches;
            if (matches.Count == 0) { OverviewAirportName.Text = "No airport found"; OverviewAirportMeta.Text = query.ToUpperInvariant(); return; }
            OverviewAirportBox.SelectedItem = matches[0];
            await LoadOverviewAirportAsync(matches[0].Icao ?? matches[0].Ident ?? query);
        }
        catch (Exception ex) { OverviewAirportName.Text = "Airport search unavailable"; OverviewAirportMeta.Text = ex.Message; }
    }

    private async Task LoadOverviewAirportAsync(string code)
    {
        var airport = await _airportData.GetAirportAsync(code);
        if (airport is null) { OverviewAirportName.Text = "Airport not found"; return; }
        OverviewAirportName.Text = airport.Name ?? airport.Icao ?? airport.Ident ?? code;
        OverviewAirportMeta.Text = string.Join("  ·  ", new[] { airport.Icao ?? airport.Ident, airport.Iata, airport.Type }.Where(v => !string.IsNullOrWhiteSpace(v)));
        OverviewAirportLocation.Text = string.Join("  ·  ", new[] { airport.Municipality, airport.Country, airport.Latitude.ToString("0.0000") + ", " + airport.Longitude.ToString("0.0000"), airport.ScheduledService ? "Scheduled service" : "No scheduled service" }.Where(v => !string.IsNullOrWhiteSpace(v)));
        OverviewAirportElevation.Text = airport.ElevationFt is double elevation ? Math.Round(elevation).ToString("N0") + " ft" : "—";
        var frequencies = airport.Frequencies ?? [];
        var ground = frequencies.FirstOrDefault(f => (f.Type ?? string.Empty).Contains("ground", StringComparison.OrdinalIgnoreCase) || (f.Description ?? string.Empty).Contains("ground", StringComparison.OrdinalIgnoreCase));
        OverviewAirportGround.Text = ground is null ? "No ground frequency reported" : ground.FrequencyMHz.ToString("0.000") + " MHz";
        OverviewAirportFrequencies.ItemsSource = frequencies.Where(f => ground is null || !ReferenceEquals(f, ground)).Take(6).Select(f => (f.Type ?? "Other") + "  ·  " + f.FrequencyMHz.ToString("0.000") + " MHz").ToList();
        OverviewAirportAtc.Text = "No airport-specific controller type is exposed by the current network API.";
        OverviewAirportSource.Text = "Source: " + (airport.Source ?? "Airly / OurAirports") + (string.IsNullOrWhiteSpace(airport.SourceUpdated) ? string.Empty : "  ·  " + airport.SourceUpdated);
    }

    private async void Weather_Click(object sender, RoutedEventArgs e)
    {
        var icao = AirportSearchBox.Text.Trim().ToUpperInvariant();
        if (icao.Length != 4) { MetarText.Text = "Enter a four-letter ICAO airport code."; return; }
        MetarText.Text = "Loading METAR…";
        WeatherDetails.Text = "";
        try
        {
            using var response = await _http.GetAsync($"{ClientConfig.ApiBaseUrl}api/weather/metar?icao={Uri.EscapeDataString(icao)}", HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode) { MetarText.Text = $"METAR unavailable (HTTP {(int)response.StatusCode})."; return; }
            var payload = await response.Content.ReadFromJsonAsync<MetarEnvelope>();
            var observation = payload?.Metar?.FirstOrDefault();
            if (observation is null) { MetarText.Text = $"No current METAR is available for {icao}."; return; }

            WeatherStation.Text = observation.IcaoId ?? icao;
            WeatherName.Text = observation.Name ?? "Airport weather station";
            WeatherCategory.Text = observation.FltCat ?? "—";
            WeatherObserved.Text = FormatUtcObservation(observation.ReportTime, observation.ObsTime);
            WeatherWind.Text = FormatWind(observation.Wdir, observation.Wspd, observation.Wgst);
            WeatherVisibility.Text = string.IsNullOrWhiteSpace(observation.Visib) ? "—" : observation.Visib + " SM";
            WeatherTemperature.Text = FormatCelsius(observation.Temp);
            WeatherDewpoint.Text = FormatCelsius(observation.Dewp);
            WeatherPressure.Text = observation.Altim is null ? "—" : $"{observation.Altim:0.0} hPa";
            WeatherClouds.Text = FormatClouds(observation.Clouds);
            WeatherPhenomena.Text = string.IsNullOrWhiteSpace(observation.WxString) ? "No significant weather reported" : observation.WxString;
            MetarText.Text = observation.RawOb ?? "Raw METAR unavailable";
            WeatherDetails.Text = $"Observed {FormatUtcObservation(observation.ReportTime, observation.ObsTime)} • {observation.Name ?? icao} • Source: AviationWeather.gov";
        }
        catch (Exception ex)
        {
            MetarText.Text = "Unable to reach the Airly weather service.";
            WeatherDetails.Text = ex.Message;
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        if (_updatePromptShown) return;
        var update = await _updateService.CheckAsync();
        if (update is null || !IsNewerVersion(update.Version, ClientConfig.Version)) return;
        _availableUpdate = update;
        var now = DateTimeOffset.UtcNow;
        if (!string.Equals(_settings.UpdateFirstSeenVersion, update.Version, StringComparison.OrdinalIgnoreCase))
        {
            _settings.UpdateFirstSeenVersion = update.Version;
            _settings.UpdateFirstSeenUtc = now;
            _settings.Save();
        }
        var firstSeen = _settings.UpdateFirstSeenUtc ?? now;
        var mandatory = now - firstSeen >= TimeSpan.FromDays(7);
        _updateIsMandatory = mandatory;
        UpdateTitle.Text = string.IsNullOrWhiteSpace(update.Name) ? "Airly Update" : update.Name;
        UpdateVersion.Text = "Version " + update.Version;
        UpdateNotes.Text = string.IsNullOrWhiteSpace(update.Notes) ? "A new Airly Client release is available." : update.Notes;
        UpdateMandatoryText.Text = mandatory ? "This update is now required. Cancel is disabled because the 7-day grace period has expired." : "You can cancel for now. Airly will require this update 7 days after it was first offered.";
        UpdateCancelButton.IsEnabled = !mandatory;
        _updatePromptShown = true;
        UpdateOverlay.Visibility = Visibility.Visible;
        UpdateOverlayTransform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(430, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private async void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is null) return;
        UpdateNowButton.IsEnabled = false;
        UpdateCancelButton.IsEnabled = false;
        UpdateNowButton.Content = "Downloading…";
        var installer = await _updateService.DownloadInstallerAsync(_availableUpdate);
        if (string.IsNullOrWhiteSpace(installer))
        {
            UpdateNowButton.IsEnabled = true;
            UpdateCancelButton.IsEnabled = !_updateIsMandatory;
            UpdateNowButton.Content = "Update now";
            return;
        }
        _settings.UpdateFirstSeenVersion = string.Empty;
        _settings.UpdateFirstSeenUtc = null;
        _settings.Save();
        UpdateNowButton.Content = "Restarting…";
        UpdateService.StartUpdater(installer);
        Close();
    }

    private void UpdateCancel_Click(object sender, RoutedEventArgs e)
    {
        if (_updateIsMandatory || !UpdateCancelButton.IsEnabled) return;
        UpdateOverlayTransform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, 430, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
        UpdateOverlay.Visibility = Visibility.Collapsed;
    }

    private static bool IsNewerVersion(string remote, string local)
    {
        return Version.TryParse(remote.TrimStart('v', 'V'), out var r) && Version.TryParse(local.TrimStart('v', 'V'), out var l) && r > l;
    }

    private void InitializeModelMatching()
    {
        _communityFolder = ModelMatchingInstaller.FindCommunityFolders().FirstOrDefault();
        ModelCommunityPath.Text = _communityFolder ?? "No default Community folder found. Use Choose Community.";
        ModelInstallButton.IsEnabled = !string.IsNullOrWhiteSpace(_communityFolder);
    }

    private void ChooseCommunity_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select your Microsoft Flight Simulator Community folder", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        var selected = dialog.FolderName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(new DirectoryInfo(selected).Name, "Community", StringComparison.OrdinalIgnoreCase))
        {
            ModelStatus.Text = "Select the Community folder itself.";
            return;
        }
        _communityFolder = selected;
        ModelCommunityPath.Text = selected;
        ModelInstallButton.IsEnabled = true;
        ModelStatus.Text = "Community folder selected.";
    }

    private void OpenCommunity_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_communityFolder) && Directory.Exists(_communityFolder))
            Process.Start(new ProcessStartInfo(_communityFolder) { UseShellExecute = true });
        else
            ModelStatus.Text = "Select a valid Community folder first.";
    }

    private async void InstallModels_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_communityFolder))
        {
            ChooseCommunity_Click(sender, e);
            if (string.IsNullOrWhiteSpace(_communityFolder)) return;
        }

        ModelInstallButton.IsEnabled = false;
        ModelProgressBar.Visibility = Visibility.Visible;
        ModelProgressBar.Value = 0;
        ModelProgressText.Text = "Preparing FSLTL download…";
        ModelStatus.Text = "Downloading the latest FSLTL Traffic Base Models release.";
        try
        {
            var progress = new Progress<double>(value =>
            {
                ModelProgressBar.Value = value;
                ModelProgressText.Text = value >= 99.9 ? "Extracting and installing into Community…" : $"Downloading… {value:0}%";
            });
            var installedPath = await _modelInstaller.InstallLatestFslTlAsync(_communityFolder!, progress);
            ModelProgressText.Text = "Installation complete.";
            ModelStatus.Text = $"FSLTL model package installed: {installedPath}";
        }
        catch (Exception ex)
        {
            ModelProgressText.Text = "";
            ModelStatus.Text = $"Model matching install failed: {ex.Message}";
        }
        finally { ModelInstallButton.IsEnabled = true; }
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        SaveSettingsFromUi();
        SettingsStatus.Text = "Settings saved. The selected theme and preferences are now active.";
    }


    private void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        LoadSettingsIntoUi();
        SettingsStatus.Text = "Staged changes reset to the last saved settings.";
    }

    private void SaveSettingsFromUi()
    {
        _settings.SimBriefPilotId = SimBriefPilotIdBox.Text.Trim();
        _settings.AirlyId = SettingsAirlyIdBox.Text.Trim();
        _settings.Username = SettingsUsernameBox.Text.Trim();
        _settings.StartWithWindows = StartWithWindowsBox.IsChecked == true;
        _settings.AutoConnect = AutoConnectBox.IsChecked == true;
        _settings.EnableAtcAudio = EnableAtcAudioBox.IsChecked == true;
        _settings.EnableMultiplayer = EnableMultiplayerBox.IsChecked == true;
        _settings.AutomaticModelMatching = AutomaticModelMatchingBox.IsChecked == true;
        _settings.ReduceAnimations = ReduceAnimationsBox.IsChecked == true;
        _settings.LimitFps = LimitFpsBox.IsChecked == true;
        _settings.LowBandwidth = LowBandwidthBox.IsChecked == true;
        _settings.HardwareAcceleration = HardwareAccelerationBox.IsChecked == true;
        _settings.CacheMapTiles = CacheMapTilesBox.IsChecked == true;
        _settings.CompactTraffic = CompactTrafficBox.IsChecked == true;
        _settings.EnableSoundEffects = EnableSoundEffectsBox.IsChecked == true;
        _settings.PushToTalk = PushToTalkBox.IsChecked == true;
        _settings.VoiceVolume = (int)Math.Round(VoiceVolumeSlider.Value);
        _settings.NetworkUpdateRate = (NetworkUpdateRateBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Balanced";
        _settings.UiScalePercent = int.TryParse(((UiScaleBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "100%").TrimEnd('%'), out var scale) ? scale : 100;
        _settings.AutomaticUpdates = AutomaticUpdatesBox.IsChecked == true;
        _settings.ShowReleaseNotes = ReleaseNotesBox.IsChecked == true;
        _settings.ThemeMode = _pendingThemeMode;
        _settings.AccentColor = _pendingAccentColor;
        AirlyIdBox.Text = _settings.AirlyId;
        UsernameBox.Text = _settings.Username;
        _settings.Save();
        ApplyTheme();
        UpdateThemeSelectionVisuals();
    }


    private void InitializeClientMetrics()
    {
        _lastFpsTick = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += CompositionTarget_Rendering;
        _metricsTimer.Tick += async (_, _) => await UpdateLatencyAsync();
        _metricsTimer.Start();
        _ = UpdateLatencyAsync();
    }

    private void CompositionTarget_Rendering(object? sender, EventArgs e)
    {
        _renderFrames++;
        var now = Stopwatch.GetTimestamp();
        var elapsed = (now - _lastFpsTick) / (double)Stopwatch.Frequency;
        if (elapsed >= 1)
        {
            var fps = _renderFrames / elapsed;
            _renderFrames = 0;
            _lastFpsTick = now;
            FpsText.Text = $"  •  FPS {fps:0}";
            HeaderFpsText.Text = $"  •  FPS {fps:0}";
        }
    }

    private async Task UpdateLatencyAsync()
    {
        if (_metricsRequestRunning) return;
        _metricsRequestRunning = true;
        try
        {
            var stopwatch = Stopwatch.StartNew();
            using var response = await _http.GetAsync($"{ClientConfig.ApiBaseUrl}api/ping", HttpCompletionOption.ResponseHeadersRead);
            stopwatch.Stop();
            if (response.IsSuccessStatusCode)
            {
                var value = $"{stopwatch.Elapsed.TotalMilliseconds:0} ms";
                LatencyText.Text = $"Latency {value}";
                HeaderLatencyText.Text = $"LATENCY {value}";
            }
            else
            {
                LatencyText.Text = "Latency —";
                HeaderLatencyText.Text = "LATENCY —";
            }
        }
        catch
        {
            LatencyText.Text = "Latency —";
            HeaderLatencyText.Text = "LATENCY —";
        }
        finally { _metricsRequestRunning = false; }
    }

    private static string FormatUtcObservation(string? reportTime, long? obsTime)
    {
        if (DateTimeOffset.TryParse(reportTime, out var parsed)) return parsed.UtcDateTime.ToString("dd MMM HH:mm'Z'");
        if (obsTime is long unix) return DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.ToString("dd MMM HH:mm'Z'");
        return "Observation time unavailable";
    }

    private static string FormatWind(int? direction, double? speed, double? gust)
    {
        if (direction is null && speed is null) return "—";
        var dir = direction is null ? "VRB" : direction.Value.ToString("000") + "°";
        var text = $"{dir} {speed.GetValueOrDefault():0} kt";
        if (gust is > 0) text += $" G{gust:0}";
        return text;
    }

    private static string FormatCelsius(double? value) => value is null ? "—" : $"{value:0.0} °C";

    private static string FormatClouds(List<CloudLayer>? clouds)
    {
        if (clouds is null || clouds.Count == 0) return "Clear / not reported";
        return string.Join("  •  ", clouds.Select(c => string.IsNullOrWhiteSpace(c.Cover) ? "Cloud layer" : $"{c.Cover} {(c.Base is null ? "" : $"{c.Base:N0} ft")}".Trim()));
    }

    private static string FormatAltitude(double feet)
    {
        if (feet <= 0) return "—";
        return $"{Math.Round(feet / 100.0) * 100:N0} ft";
    }

    private static string BuildJetPhotosSearchUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "https://www.jetphotos.com/";
        return $"https://www.jetphotos.com/search?keywords={Uri.EscapeDataString(value)}";
    }

    private sealed record MetarEnvelope(bool Ok, string? Source, string Icao, DateTimeOffset FetchedAt, List<MetarObservation>? Metar);
    private sealed record MetarObservation(string? IcaoId, string? Name, string? ReportTime, long? ObsTime, double? Temp, double? Dewp, int? Wdir, double? Wspd, double? Wgst, string? Visib, double? Altim, string? WxString, string? FltCat, string? RawOb, List<CloudLayer>? Clouds);
    private sealed record CloudLayer(string? Cover, double? Base);

    private sealed record TrackerResponse(bool Ok, string Provider, long Timestamp, int Count, List<TrackerAircraft>? Aircraft);
    private sealed record TrackerAircraft(
        [property: JsonPropertyName("icao24")] string Icao24,
        [property: JsonPropertyName("callsign")] string? Callsign,
        [property: JsonPropertyName("originCountry")] string? OriginCountry,
        [property: JsonPropertyName("latitude")] double? Latitude,
        [property: JsonPropertyName("longitude")] double? Longitude,
        [property: JsonPropertyName("altitudeFt")] double? AltitudeFeet,
        [property: JsonPropertyName("onGround")] bool OnGround,
        [property: JsonPropertyName("speedKnots")] double? SpeedKnots,
        [property: JsonPropertyName("heading")] double? Heading,
        [property: JsonPropertyName("verticalRateFpm")] double? VerticalRateFpm,
        [property: JsonPropertyName("lastContact")] double? LastContactUnix)
    {
        public string Aircraft => "Aircraft";
        public string Country => string.IsNullOrWhiteSpace(OriginCountry) ? "Unknown" : OriginCountry;
        public double GroundSpeedKnots => SpeedKnots ?? 0;
        public double HeadingDegrees => Heading ?? 0;
        public double VerticalSpeedFeetPerMinute => VerticalRateFpm ?? 0;
        public DateTimeOffset? LastContact => LastContactUnix is double unix ? DateTimeOffset.FromUnixTimeSeconds((long)unix) : null;
    }
    private sealed record NetworkStatusResponse(string Status, string Mode, int Flights, int Controllers, string Voice);

    private sealed record ActivationResponse(bool Valid, string Message);

    private sealed class AiHelperResponse
    {
        public string? reply { get; set; }
    }
}
