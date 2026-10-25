# Wisp 2.7.2

Wisp 2.7.2 ships every fix from the 2.7.1 audits: HUD layouts return to the right monitor, a clip that is already saving finishes when Forza or Wisp closes, recording waits for a library save, saved tunes stay readable across reader versions, and updates and diagnostics report what happened.

[Download the latest Wisp release for Windows](https://github.com/Views2k/Wisp/releases/latest)

## Reliability

- HUD windows move to the monitor their layout was saved on before taking their saved position, so layouts on monitors with different scaling come back on the right screen.
- The gauge update loop stops and says so when one of its passes fails five times in a row, even while the other passes keep working.
- The HUD stays hidden once its update loop has stopped, whichever hotkey or setting asks for it.
- The HUD window and renderer are rebuilt together when the renderer fails while the window is already being retried, and the window is retried when a display or the game changes while it is being updated.
- Boost and tire gauge artwork draws from its real images again as soon as they load after a temporary read failure.
- The analog tire gauge draws the minus sign of a below-freezing temperature.
- A gauge image that is missing or unreadable is reported once and drawn as a placeholder for the rest of the session; an image another program holds is retried.
- Wisp keeps running when a lossless clip's video surface cannot be created; the clip closes with its playback message.

## Runs and tunes

- Recording starts once an automatic lap save or a details save has finished, from the Record button, the shortcut, the countdown or a tune-attached start.
- A run whose final save failed is recovered and listed in Runs at once; recovered runs say “Recovered from the recording’s journal”.
- Saved tunes and attached tunes stay readable across future versions of the tune reader.
- A saved-runs warning is shown with the action it belongs to; the next refresh starts clean.
- Leftover recording files that hold nothing to recover are removed.
- Export all exports every saved run that can be read and reports how many unreadable files were kept in place.
- Tune performance distances show in metres and top speed in km/h when Wisp is set to km/h; the game’s 60 and 100 mph benchmarks keep their names.
- The Tune page says plainly that tune reading needs imperial game units when Forza uses another unit system.
- After a Forza update, the Tune page says that tune reading is waiting for a compatibility update, which Wisp checks for automatically.
- A tune read interrupted by a car or session change reports the change and asks for a refresh.

## Clips

- A clip that is already saving finishes when Forza closes, the display changes, clipping is turned off or Wisp closes.
- Clip recording stops within its time limit on slow drives instead of reporting a recorder failure.
- The Clips page shows the state of the clip shortcuts after a clip-recording pause is lifted.

## Setup and connection

- When the saved UDP port could not be bound at startup, applying a corrected port in Connection finishes starting the HUD, hotkeys, Runs and Clips.
- The setup wizard’s port guidance reads as written.
- The packet-rate display and listener restarts stay on schedule after the PC clock is set back.

## Updates

- Automatic download attempts stop after three failures for the same release, and the update card points at Update and the full installer; a newer release gets fresh attempts, and a PC without a connection waits instead of spending an attempt.
- Running the full installer by hand over an existing installation keeps the completed setup.
- An update whose automatic restart fails is recorded as installed; the next launch runs the new version.
- A completed update’s staged installer is deleted at the next launch.

## Diagnostics

- A connection report names the telemetry listener failure even after the automatic restart brought the listener back.
- Delete local logs removes native fault records as well.
- The latest native fault is recorded, so the one that ended Wisp is the one the next launch reports.
- A Windows shutdown that ends Wisp is classified from the Windows stop record when Wisp’s own shutdown notice is unavailable.
