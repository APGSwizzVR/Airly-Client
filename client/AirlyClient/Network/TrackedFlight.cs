namespace AirlyClient.Network;

public sealed record TrackedFlight(
    string Callsign,
    string Aircraft,
    string Registration,
    string Origin,
    string Destination,
    string Route,
    double Latitude,
    double Longitude,
    double AltitudeFeet,
    double GroundSpeedKnots,
    double HeadingDegrees,
    double VerticalSpeedFeetPerMinute,
    int CruiseAltitudeFeet,
    string Frequency,
    string Status,
    string PhotoUrl,
    string PhotoSourceUrl);
