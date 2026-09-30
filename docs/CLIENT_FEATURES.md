# Airly Client feature set

The desktop client is the operational application between the simulator and Airly.

## Client UI

- Flight-sim-oriented dark flight-deck interface
- Overview dashboard
- Live traffic
- Dedicated Track Flights view
- Aircraft detail and telemetry panel
- ATC frequencies
- Weather / METAR
- Charts
- SimBrief flight plan import
- Model matching
- Local client settings

## Connection

- Airly ID validation
- region selection
- simulator selection
- connect/disconnect
- authenticated realtime session
- connection/latency state
- reconnect and resynchronization

## SimBrief integration

The client stores the user's SimBrief Pilot ID locally.
The Flight Plan page has an explicit Import from SimBrief action that fetches the latest OFP only after the user clicks the button.

Imported plan data includes, where supplied by SimBrief:

- callsign
- airline and flight number
- aircraft type
- registration
- origin
- destination
- alternate
- route
- initial/cruise altitude
- schedule
- estimated flight/block time
- AIRAC cycle

The imported plan is used by the tracking view to show planned cruise altitude and route alongside live Airly telemetry.

## Flight tracking

- Search Airly traffic by callsign, aircraft or route
- live latitude / longitude
- live altitude
- cruise altitude from the imported flight plan
- ground speed
- heading
- climb/descent/cruise state
- origin and destination
- frequency
- aircraft registration when available
- JetPhotos lookup for aircraft photography

JetPhotos imagery should be supplied through an Airly-side photo service when available. The client keeps a JetPhotos search fallback rather than scraping or embedding third-party pages directly.

## Multiplayer

- live Airly aircraft
- authoritative network state
- callsign, aircraft, livery, altitude, heading, speed, vertical speed and squawk
- automatic model matching
- simulator-specific fallback models
- external traffic separation

## ATC

- controller positions
- airport/frequency list
- frequency tuning
- cockpit-to-controller voice
- hear other pilots on the same frequency
- push-to-talk
- controller handoffs
- controller session status

## Aviation tools

- global airport search
- METAR/TAF
- airport weather
- charts and procedures
- flight plans
- route validation
- airport/runway information
- NOTAM integration
- aircraft/model database
- network events

Live data must be supplied by Airly's server-side services. The client should never contain third-party API secrets.
