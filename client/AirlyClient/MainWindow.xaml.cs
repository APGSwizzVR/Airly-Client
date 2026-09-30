using System.Net.Http.Json;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
namespace AirlyClient;
public partial class MainWindow : Window
{
    private readonly HttpClient _http = new();
    private bool _validated;
    public MainWindow(){InitializeComponent();}
    private async void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        ActionButton.IsEnabled=false;
        try
        {
            var region=((ComboBoxItem)RegionBox.SelectedItem).Content?.ToString() ?? "Europe";
            var airlyId=AirlyIdBox.Text.Trim();
            if(!_validated)
            {
                if(string.IsNullOrWhiteSpace(airlyId)){StatusText.Text="Enter your Airly ID.";return;}
                StatusText.Text="Validating Airly ID…";
                var response=await _http.PostAsJsonAsync($"{ClientConfig.ApiBaseUrl}api/client/activate",new {region,airlyId});
                var payload=await response.Content.ReadFromJsonAsync<ActivationResponse>();
                if(!response.IsSuccessStatusCode || payload is null || !payload.Valid){StatusText.Text=payload?.Message ?? "Airly could not validate this ID.";return;}
                _validated=true; UsernameBox.IsEnabled=true; ActionButton.Content="Install Airly"; StatusText.Text="ID accepted. Choose your network username."; return;
            }
            if(string.IsNullOrWhiteSpace(UsernameBox.Text)){StatusText.Text="Choose a username.";return;}
            StatusText.Text="Preparing Airly simulator integration…";
            await Task.Delay(300);
            StatusText.Text="Client setup complete. Network connection services are ready to be enabled.";
            ActionButton.Content="Connected"; ActionButton.IsEnabled=false;
        }
        catch(HttpRequestException){StatusText.Text="Airly could not be reached. Check your internet connection.";}
        catch(JsonException){StatusText.Text="Airly returned an invalid activation response.";}
        catch(Exception ex){StatusText.Text=$"Setup failed: {ex.Message}";}
        finally{if(ActionButton.Content?.ToString()!="Connected") ActionButton.IsEnabled=true;}
    }
    private sealed record ActivationResponse(bool Valid,string Message);
}
