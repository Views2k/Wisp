# Wisp 2.7

Wisp 2.7 is a reliability update. Clips keep recording when the graphics card is busy, game audio records on more PCs, Wisp stays open when Windows restarts its desktop display, and updates are checked and installed more dependably.

[Download the latest Wisp release for Windows](https://github.com/Views2k/Wisp/releases/latest)

## Clips

- Clip recording keeps going when the graphics card is busy instead of restarting every few seconds, and clips stay in step with the game and its audio.
- A slow drive no longer holds up clip recording.
- Clips record game audio on PCs where recording previously stopped with an audio capture error.

## Reliability

- Wisp keeps running when Windows restarts its desktop display, for example after a graphics driver reset, and the HUD restores its transparent overlay on its own.

## Updates

- When this copy of Wisp cannot update itself automatically, Wisp stops retrying in the background and shows how to install the update with the full installer from wispoverlay.com. Install update stays available on the dashboard.
- Update checks are more dependable on shared internet connections and during network outages.

## Diagnostics

- Diagnostics tell a Windows power loss or system crash apart from a problem in Wisp.
- Crash reports include Windows crash details even when Windows is slow to respond right after sign-in.
- Game access and compatibility problems are reported once per game session.
