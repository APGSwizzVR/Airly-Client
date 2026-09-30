using System.Text.Json;

namespace AirlyClient.Network;

public sealed record SimBriefFlightPlan(
    string Callsign,
    string Airline,
    string FlightNumber,
    string Aircraft,
    string Registration,
    string Origin,
    string Destination,
    string Alternate,
    string Route,
    int InitialAltitudeFeet,
    int CruiseAltitudeFeet,
    string DepartureTime,
    string ArrivalTime,
    string AirTime,
    string BlockTime,
    string Airac,
    string SimBriefPilotId)
{
    public string RouteSummary => string.IsNullOrWhiteSpace(Route) ? "DCT" : Route;

    public static SimBriefFlightPlan FromJson(string json, string pilotId)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        static string Get(JsonElement root, params string[] path)
        {
            var current = root;
            foreach (var segment in path)
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                    return string.Empty;
            }

            return current.ValueKind switch
            {
                JsonValueKind.String => current.GetString() ?? string.Empty,
                JsonValueKind.Number => current.ToString(),
                _ => string.Empty
            };
        }

        static int GetInt(JsonElement root, params string[] path)
        {
            var value = Get(root, path);
            return int.TryParse(value, out var result) ? result : 0;
        }

        var airline = Get(root, "general", "icao_airline");
        var flightNumber = Get(root, "general", "flight_number");
        var callsign = Get(root, "atc", "callsign");

        if (string.IsNullOrWhiteSpace(callsign))
            callsign = string.IsNullOrWhiteSpace(airline) ? flightNumber : $"{airline}{flightNumber}";

        var initialAltitude = GetInt(root, "general", "initial_altitude");
        var cruiseAltitude = initialAltitude;

        var stepClimb = Get(root, "general", "stepclimb_string");
        if (!string.IsNullOrWhiteSpace(stepClimb))
        {
            var parts = stepClimb.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1 && int.TryParse(parts[^1], out var stepFlightLevel))
                cruiseAltitude = stepFlightLevel * 100;
        }

        var registration = Get(root, "aircraft", "reg");
        var aircraft = Get(root, "aircraft", "icaocode");
        if (string.IsNullOrWhiteSpace(aircraft))
            aircraft = Get(root, "aircraft", "icaotype");

        return new SimBriefFlightPlan(
            callsign,
            airline,
            flightNumber,
            aircraft,
            registration,
            Get(root, "origin", "icao_code"),
            Get(root, "destination", "icao_code"),
            Get(root, "alternate", "icao_code"),
            Get(root, "general", "route"),
            initialAltitude,
            cruiseAltitude,
            Get(root, "times", "sched_out"),
            Get(root, "times", "est_in"),
            Get(root, "times", "est_time_enroute"),
            Get(root, "times", "block_time"),
            Get(root, "params", "airac"),
            pilotId);
    }
}
