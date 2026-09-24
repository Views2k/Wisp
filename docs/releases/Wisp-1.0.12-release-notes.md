# Wisp 1.0.12

## Better diagnostic reports

Local debug logging now records telemetry reception and processing separately, dispatcher delays, native-data freshness and failures, composition gaps, focus transitions, and Wisp's CPU and memory usage. Collection runs in the background so logging continues if the interface stalls.

Exports include a readable summary: what was observed and when, which measurements support it, the likely affected component, what remains uncertain, and the next useful diagnostic step. Normal menus, hidden overlays, and ordinary telemetry disconnection are distinguished from rendering faults.

Logging is opt-in and local, runs for up to 24 hours per activation, and uses a limited amount of disk space. You decide whether to attach the ZIP to an issue. Reports cover Wisp's recorded telemetry and application timing, with observations and measurements to guide troubleshooting.

## Fixes

- Native HUD provider recovery handles a cleared secondary local-provider flag during races and settings transitions. The provider must still pass the existing contract checks and uniquely match the live car, RPM, and maximum RPM.
- Removed the outer outline from the update confirmation dialog. Release details and explicit download confirmation are unchanged.
