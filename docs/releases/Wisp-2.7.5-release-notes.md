# Wisp 2.7.5

Wisp 2.7.5 improves automatic update retries, recognizes completed clip requests during recorder shutdown, and adds sleep and lock context to session diagnostics.

[Download the latest Wisp release for Windows](https://github.com/Views2k/Wisp/releases/latest)

## Updates

- Automatic updates retry after slow or interrupted downloads and temporarily refused updater launches.
- Update installation waits while Windows is signing out or shutting down.

## Clips

- Completed clip requests are recognized when the recorder closes immediately afterward.

## Diagnostics

- Session diagnostics include sleep and lock context and identify the first HUD frame after a window retry.
