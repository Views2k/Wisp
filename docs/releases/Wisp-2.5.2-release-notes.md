# Wisp 2.5.2

Reliability fixes for the HUD, telemetry, saved runs, lap delta and updates.

## HUD

- **The HUD keeps working when Forza restarts.** If you restart Forza while Wisp stays open, the HUD comes back with the game. When the game closed, Windows cleared the HUD window's owner, and Wisp treated that as a renderer failure that turned the HUD off until Wisp restarted.
- **Diagnostics shows every HUD window's renderer failure.** A failure in one HUD window stays visible while other HUD windows run normally. Before, the last window to report replaced the others' status.

## Telemetry

- **An oversized packet no longer stops telemetry.** A packet larger than 2,048 bytes on Wisp's port is now rejected and Wisp keeps listening. Before, it stopped the listener until Wisp restarted.

## Runs

- **Details save for a run imported again after removal.** Name, tune and note changes now save. Before, they stayed in Wisp only and were lost when it closed.
- **A run imported again can be removed again.** The earlier removal's recovery copy is kept under its own name; before, its name blocked the removal.
- **The layout message reflects the save.** It says Saving layout… until the layout is written, and says when it could not be saved.

## Lap delta (beta)

- **Reset reference laps clears them at once**, including laps kept from before a Wisp restart and their saved copy. Before, it waited for the next telemetry, and laps kept from before a restart could return.
- **Reference laps are written before Wisp closes**, so they return when Wisp restarts while Forza keeps running.

## Updates

- **Confirming an update installs the version shown.** If a newer release appears while the confirmation is open, Wisp shows it for you to confirm.
