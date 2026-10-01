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
        if (sender == "You" && _activeAiChat.Title == "New chat")
        {
            var title = message.Trim();
            _activeAiChat.Title = title.Length > 42 ? title[..42].TrimEnd() + "…" : (string.IsNullOrWhiteSpace(title) ? "New chat" : title);
            AiChatList.Items.Refresh();
        }
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
        AiInput.Clear();
        AiStatusText.Text = "Thinking…";
        try
        {
            var payload = new { message = question, power = _aiPower, attachments = string.IsNullOrWhiteSpace(AiAttachment.Text) || AiAttachment.Text == "No attachment" ? Array.Empty<object>() : new[] { new { name = AiAttachment.Text, content = "Attachment selected in Airly Client" } } };
            using var response = await _http.PostAsJsonAsync($"{ClientConfig.ApiBaseUrl}api/ai/helper", payload);
            var result = await response.Content.ReadFromJsonAsync<AiHelperResponse>();
            AddAiMessage("Airly Helper", result?.reply ?? "The aviation helper service did not return a response.");
            AiStatusText.Text = response.IsSuccessStatusCode ? "Ready" : "Service unavailable";
        }
        catch (Exception ex)
        {
            AddAiMessage("Airly Helper", $"I could not reach the helper service: {ex.Message}");
            AiStatusText.Text = "Offline";
        }
        finally
        {
            AiInput.Focus();
        }
    }



    private void AiEmoji_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string emoji) { AiInput.SelectedText = emoji; AiInput.CaretIndex += emoji.Length; AiInput.Focus(); }
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



    private void AiInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            AiAsk_Click(sender, new RoutedEventArgs());
            e.Handled = true;
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
                    foreach (var chat in loaded)
                        _aiChats.Add(chat);
            }
        }
        catch { }

        if (_aiChats.Count == 0)
            _aiChats.Add(new AiChatSession { Title = "New chat" });

        _activeAiChat = _aiChats[_aiChats.Count - 1];
        AiChatList.ItemsSource = _aiChats;
        AiChatList.SelectedItem = _activeAiChat;
        RenderActiveAiChat();

        if (_activeAiChat.Messages.Count == 0)
            AddAiMessage("Airly AI", "Ask me about flight planning, ATC, aircraft systems, meteorology, navigation, procedures or aviation calculations. If it is unrelated to aviation, I’ll keep us on topic.");
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
            File.WriteAllText(_aiChatFilePath, JsonSerializer.Serialize(_aiChats, new JsonSerializerOptions { WriteIndented = true }));
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
                MaxWidth = 820,
                HorizontalAlignment = message.Sender == "You" ? HorizontalAlignment.Right : HorizontalAlignment.Left
            };
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = message.Sender == "You" ? "You" : "Airly AI",
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = message.Sender == "You" ? (Brush)FindResource("MutedBrush") : (Brush)FindResource("AccentBrush"),
                Margin = new Thickness(0, 0, 0, 6)
            });
            if (message.Sender == "You")
                stack.Children.Add(new TextBlock { Text = message.Text, FontSize = 13.5, Foreground = (Brush)FindResource("TextBrush"), TextWrapping = TextWrapping.Wrap });
            else
                stack.Children.Add(RenderAiMarkdown(message.Text));
            bubble.Child = stack;
            row.Children.Add(bubble);
            AiMessages.Children.Add(row);
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
        var chat = new AiChatSession { Title = "New chat" };
        _aiChats.Add(chat);
        _activeAiChat = chat;
        AiChatList.SelectedItem = chat;
        SaveAiChats();
        RenderActiveAiChat();
        AiInput.Focus();
    }

    private void AiChatList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AiChatList.SelectedItem is AiChatSession chat)
        {
            _activeAiChat = chat;
            RenderActiveAiChat();
        }
    }

    private sealed class AiChatSession
    {
        public string Title { get; set; } = "New chat";
        public List<AiChatMessage> Messages { get; set; } = new();
    }

    private sealed class AiChatMessage
    {
        public string Sender { get; set; } = "Airly AI";
        public string Text { get; set; } = string.Empty;
    }

    private void LoadSettingsIntoUi()
    {
        SimBriefPilotIdBox.Text = _settings.SimBriefPilotId;
        AirlyIdBox.Text = _settings.AirlyId;
        UsernameBox.Text = _settings.Username;
        SettingsAirlyIdBox.Text = _settings.AirlyId;
        SettingsUsernameBox.Text = _settings.Username;
        StartWithWindowsBox.IsChecked = _settings.StartWithWindows;
        AutoConnectBox.IsChecked = _settings.AutoConnect;
        EnableAtcAudioBox.IsChecked = _settings.EnableAtcAudio;
        EnableMultiplayerBox.IsChecked = _settings.EnableMultiplayer;
        AutomaticModelMatchingBox.IsChecked = _settings.AutomaticModelMatching;
        _settings.ThemeMode = string.Equals(_settings.ThemeMode, "Bright", StringComparison.OrdinalIgnoreCase) ? "Bright" : "Dark";
    }

    private static readonly (string Name, string Hex)[] DarkPalette =
    {
        ("Default", "#80858B"), ("Ocean", "#1689B8"), ("Aurora", "#27B8A4"), ("Violet", "#8B5CF6"),
        ("Rose", "#E05272"), ("Amber", "#D99119"), ("Emerald", "#22A06B"), ("Glacier", "#53B6D6"),
        ("Coral", "#E56A4A"), ("Silver", "#AEB7C2")
    };

    private static readonly (string Name, string Hex)[] BrightPalette =
    {
        ("Default", "#7A7F84"), ("Ocean", "#087EA4"), ("Teal", "#087F8C"), ("Violet", "#7044C8"),
        ("Rose", "#C83F75"), ("Amber", "#A96800"), ("Emerald", "#168653"), ("Sky", "#176D9C"),
        ("Coral", "#B84427"), ("Slate", "#4B5563")
    };

    private void InitializeTheme()
    {
        ThemeModeBox.SelectedItem = ThemeModeBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Content?.ToString(), _settings.ThemeMode, StringComparison.OrdinalIgnoreCase))
            ?? ThemeModeBox.Items[0];

        PopulateAccentPalette(_settings.ThemeMode, _settings.AccentColor);
        ApplyTheme();
    }

    private void ThemeModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || ThemeModeBox.SelectedItem is not ComboBoxItem item) return;
        var mode = item.Content?.ToString() == "Bright" ? "Bright" : "Dark";
        _settings.ThemeMode = mode;
        PopulateAccentPalette(mode, null);
        ApplyTheme();
    }

    private void AccentColorBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || AccentColorBox.SelectedItem is null) return;
        var hex = ExtractPaletteHex(AccentColorBox.SelectedItem.ToString());
        if (hex is null) return;
        _settings.AccentColor = hex;
        ApplyTheme();
    }

    private void PopulateAccentPalette(string mode, string? preferredHex)
    {
        var palette = string.Equals(mode, "Bright", StringComparison.OrdinalIgnoreCase) ? BrightPalette : DarkPalette;
        var target = preferredHex;
        AccentColorBox.Items.Clear();

        foreach (var entry in palette)
            AccentColorBox.Items.Add($"{entry.Name} — {entry.Hex}");

        var selected = palette.FirstOrDefault(entry =>
            string.Equals(entry.Hex, target, StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(selected.Hex))
            selected = palette[0];

        AccentColorBox.SelectedItem = $"{selected.Name} — {selected.Hex}";
        _settings.AccentColor = selected.Hex;
    }

    private static string? ExtractPaletteHex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var marker = value.LastIndexOf('—');
        if (marker < 0) return null;
        var hex = value[(marker + 1)..].Trim();
        return Regex.IsMatch(hex, "^#[0-9A-Fa-f]{6}$") ? hex : null;
    }

    private static string GetDefaultAccent(string mode) =>
        string.Equals(mode, "Bright", StringComparison.OrdinalIgnoreCase) ? BrightPalette[0].Hex : DarkPalette[0].Hex;

    private void ApplyTheme()
    {
        var bright = string.Equals(_settings.ThemeMode, "Bright", StringComparison.OrdinalIgnoreCase);
        var accent = ParseColor(_settings.AccentColor, ParseColor(GetDefaultAccent(_settings.ThemeMode), Colors.Gray));

        var window = bright ? "#F3F5F6" : "#0A0D11";
        var sidebar = bright ? "#FFFFFF" : "#080B0F";
        var glass = bright ? "#FFFFFF" : "#12171D";
        var panel = bright ? "#FAFBFC" : "#151B22";
        var input = bright ? "#F6F8F9" : "#0E1318";
        var line = bright ? "#D8E0E4" : "#27303A";
        var text = bright ? "#17232C" : "#F2F5F7";
        var muted = bright ? "#62717B" : "#9BA7B1";
        var accentSoft = Color.FromArgb(bright ? (byte)28 : (byte)48, accent.R, accent.G, accent.B);

        SetBrush("WindowBrush", window);
        SetBrush("SidebarBrush", sidebar);
        SetBrush("GlassBrush", glass);
        SetBrush("PanelBrush", panel);
        SetBrush("InputBrush", input);
        SetBrush("LineBrush", line);
        SetBrush("TextBrush", text);
        SetBrush("MutedBrush", muted);
        SetBrush("AccentBrush", accent);
        SetBrush("AccentSoftBrush", accentSoft);
        SetBrush("SuccessBrush", bright ? "#167447" : "#62C995");
        SetBrush("DangerBrush", bright ? "#B42318" : "#E68181");

        Background = (Brush)Resources["WindowBrush"];
        Foreground = (Brush)Resources["TextBrush"];
    }

    private void SetBrush(string key, string hex) =>
        Resources[key] = new SolidColorBrush(ParseColor(hex, Colors.Transparent));

    private void SetBrush(string key, Color color) =>
        Resources[key] = new SolidColorBrush(color);

    private static Color ParseColor(string hex, Color fallback) =>
        ColorConverter.ConvertFromString(hex) is Color color ? color : fallback;

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
            SaveSettingsFromUi();
            var id = AirlyIdBox.Text.Trim();
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
        SaveSettingsFromUi();
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
            UpdateCancelButton.IsEnabled = true;
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
        if (!UpdateCancelButton.IsEnabled) return;
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
        SettingsStatus.Text = "Settings saved locally.";
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
        _settings.ThemeMode = ThemeModeBox.SelectedItem is ComboBoxItem themeItem ? themeItem.Content?.ToString() ?? "Dark" : "Dark";
        _settings.AccentColor = ExtractPaletteHex(AccentColorBox.SelectedItem?.ToString()) ?? GetDefaultAccent(_settings.ThemeMode);
        AirlyIdBox.Text = _settings.AirlyId;
        UsernameBox.Text = _settings.Username;
        _settings.Save();
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
