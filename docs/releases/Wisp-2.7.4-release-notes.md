# Wisp 2.7.4

Wisp 2.7.4 reads Forza's own speedometer again on the November 5 Forza build, names the step of a failed tune read, and stops reporting normal shutdowns, slow setup tests and an offline launch as problems.

[Download the latest Wisp release for Windows](https://github.com/Views2k/Wisp/releases/latest)

## Forza compatibility

- When Forza's own speedometer is shown, Wisp reads it again on build 6.483.970.0 (Steam) and 3.483.970.0 (Xbox app / Microsoft Store): needle angle and blur, EV speed digits, power and regen fill and headlight state come from the game, as before the November 5 update.
- A compatibility check that is still running is shown as checking, not as an unsupported game build.

## Tunes

- A tune read failure report names the check that rejected the car.
- A car whose part data leaves a part level unresolved is read; the snapshot is marked incomplete instead of failing.
- Tune Performance results stay with the current setup while the parts and settings are unchanged.

## HUD

- When the game's own speedometer is hidden, the gauge reader waits 250 ms between attempts instead of retrying on every HUD update.
- On the Microsoft Store build, a HUD window whose host window failed at alt-tab or game attach is rebuilt at the next attach instead of staying blank until Wisp restarts; the report names the failing Windows call.

## Reporting

- A run that ended with a Windows shutdown or sign-out no longer shows a crash card.
- The setup wizard's Data Out test no longer records its 45-second guidance checkpoint as a failed test.
- An update check that runs before the PC is connected is retried after two minutes and not reported as a failure.
