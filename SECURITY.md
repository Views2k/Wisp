# Security policy

## Supported versions

Security fixes are provided for the latest Wisp release. Reproduce a report on
the newest version before submitting it when possible.

## Reporting a vulnerability

Use **Report a vulnerability** on the repository's Security tab so details stay
private while the issue is investigated. If private vulnerability reporting is
not available, open a minimal issue requesting a private contact channel; do not
include exploit details in that issue.

Include:

- the affected Wisp version and Windows version;
- a concise impact statement and reproducible steps;
- the smallest safe test case or diagnostic record needed to confirm the issue;
- whether the issue affects the installer, local telemetry, settings, the
  read-only FH6 compatibility provider, the compatibility-pack verifier, or the
  application updater.

Remove account data, local paths, tokens, and other personal information from
all reports. Do not attach game executables, save files, or other copyrighted
game data.

Wisp does not need administrator access, accepts telemetry only on loopback, and
opens the supported FH6 process with query/read access only. A report that shows
behavior outside those boundaries is security-sensitive even if no user data is
exposed.

## Application-update boundary

Wisp checks for application updates only after the user selects **Check for
updates**. The client makes an anonymous HTTPS request to the latest-release API
and accepts only a non-draft, non-prerelease, immutable release with a strict
version tag. The release must contain exactly one canonical versioned installer
asset with an uploaded state, byte length, and GitHub SHA-256 digest.

Redirects are handled explicitly and are limited to GitHub's HTTPS release-asset
hosts. The downloaded bytes must match both the recorded length and digest. A
separate staged helper independently validates the installer, waits for the
exact Wisp process to exit, applies the current-user Inno Setup package, verifies
the installed executable and version, and restarts Wisp. A response or download
that fails validation is rejected before the installer starts. If installation
or post-install validation fails after setup begins, the helper reports the
failure; recovery can require rerunning the verified installer.

No GitHub token or other update credential is stored in the application. The
installer is not Authenticode-signed; SHA-256 validation establishes artifact
identity but does not replace publisher signing.
