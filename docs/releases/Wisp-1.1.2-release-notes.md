# Wisp 1.1.2

Compatibility update for the Steam FH6 `6.440.853.0` build, with a signed
compatibility-map channel for future reviewed game updates.

- Adds a separate native memory map for the new Steam executable.
- Retains the verified Steam `6.430.771.0` and Xbox app / Microsoft Store
  `3.430.771.0` maps. New Store builds require their own reviewed map.
- Enables background and manual checks for signed maps, including bundles
  covering multiple game builds. Accepted maps remain available offline.
- Preserves exact executable/package checks, bounded read-only access, and
  menu visibility validation. Unknown layouts are never guessed.

Some future game changes will still require an application update when they
change the reader's supported data structures or semantics.

The installer is `Wisp-Setup-1.1.2.exe`. Install over your current Wisp version
to retain completed setup, HUD preferences, profiles, and tire calibration.
