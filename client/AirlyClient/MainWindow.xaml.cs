using System.Collections.ObjectModel;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using AirlyClient.Network;

namespace AirlyClient;

public partial class MainWindow : Window
{
    private readonly HttpClient _http = new();
    private readonly ObservableCollection<AircraftState> _traffic = new();
    private bool _connected;

    public MainWindow()
    {
        InitializeComponent();
        TrafficGrid.ItemsSource = _traffic;
        LoadDemoUiState();
    }

    private void LoadDemoUiState()
    {
        FrequencyGrid.ItemsSource = new[]
        {
            new { Airport="EGLL", Position="London Ground", Frequency="121.700", Controller="—"},
            new { Airport="EIDW", Position="Dublin Tower", Frequency="118.600", Controller="—"}
        };
        ChartList.ItemsSource = new[] {"Airport diagram", "SID", "STAR", "Approach", "Ground / taxi", "Other procedures"};
        ModelStatus.Text = "Ready — server catalog will be loaded after network authentication.";
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        var name = (sender as Button)?.Tag?.ToString() ?? "Dashboard";
        var views = new Dictionary<string, UIElement>
        {
            ["Dashboard"]=DashboardView, ["Connect"]=ConnectView, ["Traffic"]=TrafficView,
            ["Frequencies"]=FrequenciesView, ["Weather"]=WeatherView, ["Charts"]=ChartsView,
            ["FlightPlan"]=FlightPlanView, ["Models"]=ModelsView, ["Settings"]=SettingsView
        };
        foreach (var view in views.Values) view.Visibility = Visibility.Collapsed;
        views[name].Visibility = Visibility.Visible;
        PageTitle.Text = name switch { "Frequencies"=>"ATC Frequencies", "FlightPlan"=>"Flight Plan", "Models"=>"Model Matching", "Weather"=>"Weather / METAR", _=>name };
        PageSubtitle.Text = name switch { "Traffic"=>"Live Airly multiplayer traffic", "Frequencies"=>"Controller positions and frequency audio", "Weather"=>"Airport weather and METAR information", "Charts"=>"Airport and instrument procedure charts", _=>"Airly network tools" };
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_connected) { Disconnect(); return; }
        ConnectButton.IsEnabled = false;
        ConnectStatus.Text = "Validating Airly membership…";
        try
        {
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
        NetworkStatus.Text = connected ? "Online" : "Offline";
        OwnAircraft.Text = connected ? "Ready" : "Offline";
        ConnectButton.Content = connected ? "Disconnect" : "Connect";
        TrafficCount.Text = _traffic.Count.ToString();
        ActivityText.Text = connected ? "Connected. Traffic, ATC frequencies, voice and simulator state can now synchronize through the Airly realtime service." : "Connect to Airly to receive live traffic, ATC and network data.";
    }

    private async void Weather_Click(object sender, RoutedEventArgs e)
    {
        var icao = AirportSearchBox.Text.Trim().ToUpperInvariant();
        if (icao.Length != 4) { MetarText.Text = "Enter a four-letter ICAO airport code."; return; }
        MetarText.Text = "Loading METAR…";
        try
        {
            var response = await _http.GetAsync($"{ClientConfig.ApiBaseUrl}api/weather/metar?icao={Uri.EscapeDataString(icao)}");
            if (!response.IsSuccessStatusCode) { MetarText.Text = "METAR service is not connected yet."; return; }
            var data = await response.Content.ReadAsStringAsync();
            MetarText.Text = data;
            WeatherDetails.Text = "Airly will use a server-side weather provider so provider credentials are never shipped inside the client.";
        }
        catch { MetarText.Text = "Unable to reach the Airly weather service."; }
    }

    private sealed record ActivationResponse(bool Valid, string Message);
}