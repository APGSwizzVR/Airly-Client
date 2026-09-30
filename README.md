# Airly Client

Official Windows client for the Airly aviation network.

The client will provide simulator connectivity, multiplayer aircraft synchronization, built-in model matching, flight plans, controller frequency subscriptions, and authenticated cockpit/controller voice.

The authoritative account database, permissions, network state and voice services remain server-side.

## Build

Open GitHub Actions, select `Build Airly Client`, run it, then download the `Airly-Client-win-x64` artifact. The workflow produces a self-contained Windows EXE.

## Security

The client does not contain database credentials, Discord bot tokens, private API keys, signing keys, or privileged network credentials.
