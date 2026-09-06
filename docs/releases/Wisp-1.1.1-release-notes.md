# Wisp 1.1.1

Wisp now supports the Xbox app and Microsoft Store Windows PC edition of
Forza Horizon 6, alongside Steam.

## Xbox app and Microsoft Store compatibility

- Adds Native HUD support for FH6 PC build `3.430.771.0` with a separate,
  build-specific compatibility contract.
- Validates the Store installation through Windows package identity and bounded
  checks of the loaded game image, without requiring a file hash of the protected
  executable.
- Handles the supported installation's Windows path aliases by confirming they
  identify the same file, while retaining the existing process and read-only
  data checks.
- Uses the same local Data Out setup as Steam. No administrator access, file
  permission changes, or game modifications are required.

Steam FH6 build `6.430.771.0` remains supported through its existing compatibility
path. Both storefronts require their matching reviewed build; an unfamiliar game
build is not treated as compatible automatically.

This support is for FH6 installed locally on a Windows PC, not Xbox consoles or
Xbox Cloud Gaming.

## Updating

Use **Check for updates** in Wisp's Extras page, or download the latest installer
from [GitHub Releases](https://github.com/Views2k/Wisp/releases/latest).
The release tag is `v1.1.1` and the installer is `Wisp-Setup-1.1.1.exe`, preserving
update discovery for existing installations.

HUD layouts, colors, profiles, tire calibration, and speed-smoothing behavior
are unchanged from Wisp 1.1.
