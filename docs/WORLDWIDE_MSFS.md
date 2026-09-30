# Airly MSFS integration

Airly is an out-of-process Windows client. It does not install Airly packages, aircraft, scenery, WASM modules or other files into the MSFS Community folder or simulator installation.

The architecture targets both Microsoft Store/Xbox-app PC and Steam PC installations through SimConnect. The client remains in the Airly application directory and uses the simulator's external SimConnect interface.

Xbox console is not a Windows EXE target. The Windows client supports the PC versions only.

The installer must write Airly files only to the Airly application directory and `%LOCALAPPDATA%\\Airly`. It must never silently modify an MSFS installation.
