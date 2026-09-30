using System.Net.Http;
using System.Net.Http.Json;

namespace AirlyClient;

public sealed class AirportDataService
{
    private readonly HttpClient _http;
    public AirportDataService(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<FrequencyRecord>> GetFrequenciesAsync(string icao, CancellationToken cancellationToken = default)
    {
        var code = icao.Trim().ToUpperInvariant();
        var result = await _http.GetFromJsonAsync<FrequencyResponse>($"{ClientConfig.ApiBaseUrl}api/airports/{Uri.EscapeDataString(code)}/frequencies", cancellationToken);
        return result?.Frequencies ?? [];
    }

    public async Task<ChartResponse?> GetChartsAsync(string icao, CancellationToken cancellationToken = default)
    {
        var code = icao.Trim().ToUpperInvariant();
        return await _http.GetFromJsonAsync<ChartResponse>($"{ClientConfig.ApiBaseUrl}api/charts/{Uri.EscapeDataString(code)}", cancellationToken);
    }

    public sealed record FrequencyResponse(bool Ok, string? Source, AirportSummary? Airport, List<FrequencyRecord>? Frequencies);
    public sealed record AirportSummary(string? Ident, string? Icao, string? Name);
    public sealed record FrequencyRecord(string? Id, string? Type, string? Description, double FrequencyMHz);
    public sealed record ChartResponse(bool Ok, AirportSummary? Airport, List<ChartSource>? Charts, string? Note);
    public sealed record ChartSource(string? Region, string? Provider, string? Coverage, string? Url, bool DirectPdf);
}
