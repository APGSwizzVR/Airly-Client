using System.Net.Http;
using System.Net.Http.Json;

namespace AirlyClient;

public sealed class AirportDataService
{
    private readonly HttpClient _http;
    public AirportDataService(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<AirportSearchResult>> SearchAirportsAsync(string query, CancellationToken cancellationToken = default)
    {
        var q = query.Trim();
        if (q.Length < 2) return [];
        var result = await _http.GetFromJsonAsync<AirportSearchResponse>($"{ClientConfig.ApiBaseUrl}api/airports/search?q={Uri.EscapeDataString(q)}", cancellationToken);
        return result?.Airports ?? [];
    }

    public async Task<AirportOverview?> GetAirportAsync(string icao, CancellationToken cancellationToken = default)
    {
        var code = icao.Trim().ToUpperInvariant();
        var result = await _http.GetFromJsonAsync<AirportDetailResponse>($"{ClientConfig.ApiBaseUrl}api/airports/{Uri.EscapeDataString(code)}", cancellationToken);
        return result?.Airport;
    }

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

    public sealed record AirportSearchResponse(bool Ok, string? Source, string? Query, List<AirportSearchResult>? Airports);
    public sealed record AirportSearchResult(string? Ident, string? Icao, string? Iata, string? Name, string? Municipality, string? Country, string? Type, double Latitude, double Longitude, double? ElevationFt, bool ScheduledService)
    {
        public string Label => $"{Icao ?? Ident ?? "----"}  ·  {Name ?? "Unknown airport"}" + (string.IsNullOrWhiteSpace(Municipality) ? string.Empty : $"  ·  {Municipality}");
    }
    public sealed record AirportDetailResponse(bool Ok, AirportOverview? Airport);
    public sealed record AirportOverview(string? Ident, string? Icao, string? Iata, string? Name, string? Municipality, string? Country, string? Type, double Latitude, double Longitude, double? ElevationFt, bool ScheduledService, string? OfficialWebsite, List<FrequencyRecord>? Frequencies, List<RunwayRecord>? Runways, string? Source, string? SourceUpdated);
    public sealed record FrequencyResponse(bool Ok, string? Source, AirportSummary? Airport, List<FrequencyRecord>? Frequencies);
    public sealed record AirportSummary(string? Ident, string? Icao, string? Name);
    public sealed record FrequencyRecord(string? Id, string? Type, string? Description, double FrequencyMHz);
    public sealed record RunwayRecord(double? LengthFt, double? WidthFt, string? Surface, bool Closed, bool Lighted, string? LowIdent, string? HighIdent, double? LowHeading, double? HighHeading);
    public sealed record ChartResponse(bool Ok, AirportSummary? Airport, List<ChartSource>? Charts, string? Note);
    public sealed record ChartSource(string? Region, string? Provider, string? Coverage, string? Url, bool DirectPdf);
}
