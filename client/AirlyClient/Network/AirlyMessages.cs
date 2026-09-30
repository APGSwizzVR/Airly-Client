using System.Text.Json.Serialization;

namespace AirlyClient.Network;

public sealed record AirlyEnvelope(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("requestId")] string? RequestId,
    [property: JsonPropertyName("payload")] object? Payload);

public static class AirlyMessageTypes
{
    public const string Hello = "hello";
    public const string Authenticate = "authenticate";
    public const string JoinNetwork = "join_network";
    public const string AircraftSnapshot = "aircraft_snapshot";
    public const string AircraftUpdate = "aircraft_update";
    public const string FlightPlan = "flight_plan";
    public const string ControllerSession = "controller_session";
    public const string FrequencyJoin = "frequency_join";
    public const string FrequencyLeave = "frequency_leave";
    public const string Handoff = "handoff";
    public const string Heartbeat = "heartbeat";
    public const string Disconnect = "disconnect";
}
