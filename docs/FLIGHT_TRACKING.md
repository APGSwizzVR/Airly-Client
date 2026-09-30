# Airly Client flight tracking design

The Track Flights screen is intended to become the desktop counterpart to Airly's public tracker.

Each tracked aircraft should expose authoritative Airly state:

- callsign
- aircraft/model
- registration
- latitude / longitude
- altitude
- ground speed
- heading
- vertical speed
- frequency
- network status

When a user has imported a SimBrief OFP, Airly can correlate the callsign and add planned:

- origin
- destination
- route
- cruise altitude
- scheduled times
- aircraft registration

## Aircraft photography

JetPhotos is the preferred photography source for tracked aircraft. The client currently exposes a JetPhotos lookup fallback. For production, Airly should provide a server-side /api/aircraft/photo endpoint that returns a cached JetPhotos image URL and attribution metadata.

This keeps JetPhotos access out of the realtime client and gives Airly control over caching, rate limits, failures, and future provider changes.

## SimBrief

SimBrief's documented latest-OFP fetch endpoint accepts a Pilot ID and supports JSON output. The client only calls it after the user presses Import from SimBrief; it does not poll SimBrief automatically.

## Future realtime behavior

The current client UI is ready for NetworkSession snapshots. Once the realtime receiver is wired in, incoming AircraftState updates should refresh the tracking collection and preserve the selected aircraft when possible.

Do not treat the imported SimBrief plan as authoritative aircraft position. SimBrief provides planned flight data; Airly realtime state provides current network position and telemetry.
