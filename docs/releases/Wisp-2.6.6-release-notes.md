# Wisp 2.6.6

[Download the latest published Wisp release for Windows](https://github.com/Views2k/Wisp/releases/latest)

## Wisp Tools

- Send **Diagnostics**, **Suggestions** or **Feedback** from Extras. Review the exact message before sending, cancel before submission and keep the confirmation reference.
- One message is accepted per installation each UTC day. Wisp shows when another can be sent. Retrying an uncertain submission keeps the same message identity.

## Automatic diagnostics

- Wisp now uses a live API for automatic diagnostic collection: app health, feature outcomes, connection states, Clips stages, timing summaries, process CPU and memory use, and session durations.
- Random installation and session identifiers link these observations. **There is no in-app opt-out.** Detailed event and support content is retained for **30 days**; download history is retained separately.
- Reports include Windows version, processor count, memory capacity, GPU model and driver, display settings, Wisp settings and power context. They also record the presence of Discord, RivaTuner Statistics Server, Xbox Game Bar, OBS, SimHub or Overwolf to help compare affected setups.
- Failure evidence uses fixed error codes, compiled Wisp method fingerprints and up to three preceding performance samples from 30 seconds. A matching build catalog resolves those fingerprints. Settings-load failures, audio failures, encoding failures, native access denial and unsupported game builds remain distinct observations.
- Activity summaries include moving and stationary time, estimated distance, average speed and peak observed speed, using fresh supported gameplay measurements.
- Gameplay packets, saved runs, tunes, clips, personal paths, hardware serial numbers, full process lists, raw exception messages and raw diagnostic logs are not uploaded automatically. A message you write in Extras is sent only after review and confirmation.

- Reporting tolerates incorrect PC clocks using a server-time check, while keeping the original observation time. A damaged notice cache no longer disables diagnostics or support.

[Privacy details](https://wispoverlay.com/legal/#privacy).

## Developer notices

- Verified notices can update **A note from Views** independently of the dashboard banner, while keeping the note's existing timing and close controls.
- A custom dashboard banner contains text only. When it expires or is withdrawn, the normal automatic update notice returns. Update controls remain available in Extras.
