# Wisp 2.6.13

[Download the latest Wisp release for Windows](https://github.com/Views2k/Wisp/releases/latest)

## Reliability

- The HUD keeps running when the graphics card or Windows compositor refuses memory as the game starts; the renderer reports it and restarts instead of closing Wisp.
- Closing Wisp while a renderer thread is stuck in a driver call ends the app cleanly instead of crashing.
- When the update helper is missing, Wisp shows the repair instructions once and stops automatic update attempts for the session. Install update stays available on the dashboard.

## Diagnostics

- Wisp reports whether the drift gauge is switched on, so Wisp Tools shows feature use accurately.
- Crash reports name the faulting module, its version and offset, and include the call stack Wisp captured at the fault.
