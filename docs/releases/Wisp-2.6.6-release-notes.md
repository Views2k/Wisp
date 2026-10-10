# Wisp 2.6.6

[Download Wisp 2.6.6 for Windows](https://github.com/Views2k/Wisp/releases/download/v2.6.6/Wisp-Setup-2.6.6.exe)

## Wisp Tools

- Send **Diagnostics**, **Suggestions** or **Feedback** from Extras. Review the exact message before sending, cancel before submission and keep the confirmation reference.
- One message is accepted per installation each UTC day. Wisp shows when another can be sent. Retrying an uncertain submission keeps the same message identity.

## Automatic diagnostics

- Wisp now sends bounded app health and usage measurements to the Wisp Tools service automatically. These include observed feature outcomes, connection states, Clips stages, timing summaries, process CPU and memory measurements, and observed session durations.
- Random installation and session identifiers link these observations. **There is no in-app opt-out.** Detailed event and support content is retained for **30 days**; download history is retained separately.
- Failure evidence uses fixed error codes, selected Wisp method names and up to three preceding performance samples from 30 seconds. Audio failures, encoding failures, native access denial and unsupported game builds remain distinct observations.
- Gameplay packets, saved runs, tunes, clips, personal paths, raw exception messages and raw diagnostic logs are not uploaded automatically. A message you write in Extras is sent only after review and confirmation.

These measurements help compare failures and performance; they do not establish an exact root cause. Work timings and callbacks are not displayed-frame or end-to-end latency measurements. [Privacy details](https://wispoverlay.com/legal/#privacy).

## Developer notices

- Verified notices can update **A note from Views** independently of the dashboard banner, while keeping the note's existing timing and close controls.
- A custom dashboard banner contains text only. When it expires or is withdrawn, the normal automatic update notice returns. Update controls remain available in Extras.
