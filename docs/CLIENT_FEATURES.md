# Airly Client feature set

The desktop client is the operational application between the simulator and Airly.

## Connection
- Airly ID validation
- region selection
- simulator selection
- connect/disconnect
- authenticated realtime session
- connection/latency state
- reconnect and resynchronization

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
