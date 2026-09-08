# Wisp 1.1.3

Adds Xbox app / Microsoft Store FH6 `3.440.853.0` support on Windows PC and
optional vacuum pressure for boost gauges, and fixes automatic update checks.

- Includes a reviewed native HUD compatibility map for the updated Store build.
  Previous Store `3.430.771.0` and existing Steam maps remain available offline.
- Retains signed compatibility-map updates introduced in 1.1.2, with exact
  package and code checks, atomic installation, and offline reuse. Future reviewed
  maps within the supported reader can be delivered without reinstalling Wisp;
  changes to native layouts can still require an application update.

- Enable **Show vacuum pressure** in Appearance > Boost to show negative pressure
  reported by FH6. The option is off by default. Pressure readings first require
  positive boost from the current car/session; vacuum alone does not enable them.
- Keep the boost gauge enabled even if you do not know whether a tune has forced
  induction. Naturally aspirated cars show a stationary zero gauge. Electric
  vehicles never show the boost gauge.
- Supports Digital and Analogue gauges, attached and detached layouts, and the
  Appearance preview in PSI or bar.
- Uses a -20 to 70 PSI or -1 to 5 bar scale when enabled. The Digital rail marks
  zero between vacuum and positive boost.
- Saves the option with Wisp settings and HUD profiles. Existing settings and
  profiles leave vacuum disabled.

- With automatic checks enabled, Wisp checks for application updates whenever
  it opens and every 24 hours while it remains running, including in the tray.
  A recent check from an earlier launch no longer suppresses the startup check.
- An available-update banner stays visible if a later automatic refresh fails.
  Downloads and installation still require confirmation.

The installer is `Wisp-Setup-1.1.3.exe`. Install over your current Wisp version
to retain completed setup, HUD preferences, profiles, and tire calibration.
