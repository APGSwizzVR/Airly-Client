using System.IO;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AirlyClient.Network;

namespace AirlyClient;

public partial class MainWindow : Window
{
    private readonly HttpClient _http = new();
    private readonly AirportDataService _airportData;
    private readonly ObservableCollection<AircraftState> _traffic = new();
    private readonly ObservableCollection<TrackedFlight> _trackedFlights = new();
    private readonly AppSettings _settings;
    private bool _connected;
    private TrackedFlight? _selectedFlight;
    private SimBriefFlightPlan? _flightPlan;
    private readonly DispatcherTimer _metricsTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly ModelMatchingInstaller _modelInstaller;
    private string? _communityFolder;
    private bool _metricsRequestRunning;
    private int _renderFrames;
    private long _lastFpsTick;

    public MainWindow()
    {
        InitializeComponent();
        _settings = AppSettings.Load();
        _airportData = new AirportDataService(_http);
        _modelInstaller = new ModelMatchingInstaller(_http);
        TrafficGrid.ItemsSource = _traffic;
        TrackingGrid.ItemsSource = _trackedFlights;
        LoadSettingsIntoUi();
        InitializeTheme();
        LoadDemoUiState();
        InitializeClientMetrics();
        InitializeModelMatching();
        TryLoadApplicationIcon();
        RefreshTracking();
        Closed += (_, _) => _settings.Save();
        Closed += (_, _) => CompositionTarget.Rendering -= CompositionTarget_Rendering;
        AddAiMessage("Airly AI", "Ask me about flight planning, ATC, aircraft systems, meteorology, navigation, procedures or aviation calculations. If it is unrelated to aviation, I’ll keep us on topic.");
    }

    private void AddAiMessage(string sender, string message)
    {
        var border = new Border { Background = sender == "You" ? System.Windows.Media.Brushes.Transparent : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(16,27,45)), BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(38,55,80)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Padding = new Thickness(12), Margin = new Thickness(0,0,0,8) };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = sender.ToUpperInvariant(), FontSize = 9, FontWeight = FontWeights.Bold, Foreground = sender == "You" ? System.Windows.Media.Brushes.LightSkyBlue : System.Windows.Media.Brushes.LightGray });
        stack.Children.Add(new TextBlock { Text = message, FontSize = 12, Foreground = System.Windows.Media.Brushes.White, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,5,0,0) });
        border.Child = stack;
        AiMessages.Children.Add(border);
        AiScroll.ScrollToEnd();
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
            var payload = new { message = question, attachments = string.IsNullOrWhiteSpace(AiAttachment.Text) || AiAttachment.Text == "No attachment" ? Array.Empty<object>() : new[] { new { name = AiAttachment.Text, content = "Attachment selected in Airly Client" } } };
            using var response = await _http.PostAsJsonAsync($"{ClientConfig.ApiBaseUrl}api/ai/helper", payload);
            var result = await response.Content.ReadFromJsonAsync<AiHelperResponse>();
            AddAiMessage("Airly Helper", result?.reply ?? "The aviation helper service did not return a response.");
            AiStatusText.Text = response.IsSuccessStatusCode ? "Ready" : "Service unavailable";
        }
        catch (Exception ex) { AddAiMessage("Airly Helper", $"I could not reach the helper service: {ex.Message}"); AiStatusText.Text = "Offline"; }
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
        ("Default", "#8A96A3"), ("Ocean", "#36A3FF"), ("Aurora", "#45D6C5"), ("Violet", "#A78BFA"),
        ("Rose", "#FB7185"), ("Amber", "#F4B740"), ("Emerald", "#42C98A"), ("Glacier", "#6ED0F0"),
        ("Coral", "#FF8A65"), ("Silver", "#B8C7D9")
    };

    private static readonly (string Name, string Hex)[] BrightPalette =
    {
        ("Default", "#5C6670"), ("Ocean", "#075EAA"), ("Teal", "#087F8C"), ("Violet", "#6842B8"),
        ("Rose", "#C43D61"), ("Amber", "#996000"), ("Emerald", "#167447"), ("Sky", "#176D9C"),
        ("Coral", "#B84427"), ("Slate", "#475569")
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
        new ColorConverter().ConvertFromString(hex) is Color color ? color : fallback;

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
            ["Dashboard"] = DashboardView, ["Connect"] = ConnectView, ["Traffic"] = TrafficView, ["AiHelper"] = AiHelperView,
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
            "Tracking" => "Search and inspect aircraft on the Airly network",
            "FlightPlan" => "Import the latest operational flight plan from SimBrief",
            "Frequencies" => "Controller positions and authenticated frequency audio",
            "Weather" => "Airport weather and METAR information",
            "Charts" => "Airport and instrument procedure charts",
            "AiHelper" => "A focused assistant for aviation questions and calculations",
            _ => "Flight simulation network operations"
        };
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
        TrafficCount.Text = _traffic.Count.ToString();
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

        foreach (var aircraft in _traffic)
        {
            var planMatches = _flightPlan is not null &&
                              string.Equals(aircraft.Callsign, _flightPlan.Callsign, StringComparison.OrdinalIgnoreCase);

            _trackedFlights.Add(new TrackedFlight(
                aircraft.Callsign,
                aircraft.ModelCode,
                planMatches ? _flightPlan!.Registration : string.Empty,
                planMatches ? _flightPlan!.Origin : "—",
                planMatches ? _flightPlan!.Destination : "—",
                planMatches ? _flightPlan!.RouteSummary : "—",
                aircraft.Latitude,
                aircraft.Longitude,
                aircraft.AltitudeFeet,
                aircraft.GroundSpeedKnots,
                aircraft.HeadingDegrees,
                aircraft.VerticalSpeedFeetPerMinute,
                planMatches ? _flightPlan!.CruiseAltitudeFeet : 0,
                aircraft.Frequency ?? "—",
                aircraft.VerticalSpeedFeetPerMinute > 300 ? "CLIMB" : aircraft.VerticalSpeedFeetPerMinute < -300 ? "DESCENT" : "CRUISE",
                string.Empty,
                BuildJetPhotosSearchUrl(planMatches ? _flightPlan!.Registration : aircraft.Callsign)));
        }
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
            ? _selectedFlight.Aircraft
            : $"{_selectedFlight.Aircraft} • {_selectedFlight.Registration}";
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

    private void TryLoadApplicationIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Airly.ico");
            if (File.Exists(iconPath))
                Icon = new BitmapImage { UriSource = new Uri(iconPath, UriKind.Absolute), CacheOption = BitmapCacheOption.OnLoad };
        }
        catch { }
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

    private sealed record ActivationResponse(bool Valid, string Message);

    private sealed class AiHelperResponse
    {
        public string? reply { get; set; }
    }
}
