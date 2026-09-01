# Compatibility and Update Safety

## Current support

The current Wisp build contains a Native HUD compatibility contract for the Steam
FH6 build `6.430.771.0` and its recorded executable fingerprint. Support is
fingerprint-specific; Wisp does not assume that another storefront binary with
the same version string is compatible.

Data Out speed and G-force use the local 324-byte UDP packet. Exact Native
redline, assist, and electric-gauge state use a separate guarded read-only path
because those values are not fully represented in Data Out. Data Out reception,
calculations, and dashboard diagnostics do not depend on that provider. Driving
overlays still require validated Native gameplay visibility so they cannot leak
into menus or cutscenes; when visibility is unavailable, the overlays fail
closed even if valid UDP data continues to arrive.

No implementation can safely promise support for every undocumented game
update. Wisp treats an unknown executable as incompatible instead of applying
offsets from a different build.

## New cars, tunes, and content

Wisp has no car whitelist or redline database. A positive Data Out car ID is
matched to the unique current local-player source, and redline and tach maximum
come from that vehicle's live model.

A new car or tune that uses the supported runtime schema does not need a new
lookup row. A content update that also changes the executable fingerprint still
requires a reviewed compatibility contract. Changed packet fields, powertrain
semantics, or renderer behavior can require an application update rather than a
data-only contract.

Car changes, activity transitions, maximum-RPM changes, and telemetry rewinds
invalidate in-flight Native state. A return to an earlier car cannot publish a
reading from the previous session.

## Read-only Native provider

Before accepting Native state, Wisp validates:

- executable version, byte length, SHA-256, and image size;
- compatibility schema and bounded module-relative addresses;
- process generation, executable path, and module identity;
- vtable guards and field alignment;
- a unique local-player source;
- Data Out car identity, current RPM, and maximum RPM agreement.

The electric-gauge path adds a bounded registry traversal with exact wrapper,
context, HUD, subobject, outer-control, child, and provider guards. Its child
snapshot is copied as one block and the ownership chain is checked again before
publication. Final speed digits must be decimal values, fade flags must be
strict booleans, and power, regeneration, ratio, gear, needle, and scale fields
must remain inside their source-proven ranges.

The process handle requests query/read permissions only. The production source
has no path for process-memory writes, code injection, remote threads, debugger
attachment, drivers, or game-function calls.

Tach, assist, and Native electric-gauge capabilities remain separate after
their shared identity checks. Invalid redline state does not remove validated
assists, and an unavailable electric child does not turn a provider fallback
into a fabricated needle or gear. Shared identity failures invalidate all
process-derived state. UDP speed and G-force calculation and dashboard reporting
remain available whenever their own inputs are valid, but visible driving
overlays also require the separate gameplay-visibility capability.

FH6's electric child stores the final displayed hundreds, tens, and ones digits
after the game's active display-unit conversion, together with its three
leading-digit fade flags. Wisp exposes that state only for the matching electric
Native modes. The native unit must resolve to MPH or KM/H, and the selected Wisp
unit must match it. Wisp does not reinterpret those digits as wheel-indicated
speed or silently relabel them with an unrelated unit selection.

## Compatibility contracts

A contract contains data only: executable identity, bounded module-relative
addresses, field offsets, widths, guards, and thresholds. It cannot contain
executable code, commands, URLs, or trust keys.

The runtime includes strict validation for signed contracts, revision floors,
an offline cache, and an HTTPS client. Automatic distribution is not configured
in the current build: the production endpoint and publisher keys are empty, so the
application makes no compatibility-update request and keeps import/check
controls disabled. Recovery from a new or changed executable fingerprint
therefore requires a reviewed Wisp release containing a compatible contract.

Distribution can be enabled only with pinned release-owned keys and reviewed
contracts. A failed download or signature check leaves the accepted catalog
unchanged. No vehicle data is uploaded.

This small pinned-key protocol is not a complete software-update framework. It
does not claim protection from a hostile local administrator, application
replacement, local cache rollback by the same user, or system-clock tampering.

## Application updates

The application updater is separate from compatibility contracts. It runs only
when the user selects **Check for updates**; it does not poll in the background.
The client uses GitHub's anonymous latest-release endpoint and requires a
non-draft, non-prerelease, immutable release with a strict `vX.Y.Z` tag. Exactly
one uploaded `Wisp-Setup-<version>.exe` asset must match the tag and provide its
byte length and GitHub SHA-256 digest.

The initial download URL and every redirect must remain on the allowlisted
GitHub HTTPS release path. Wisp verifies the received length and digest before
offering to install. A staged helper repeats the artifact and process checks,
waits for Wisp to exit, runs the current-user Inno installer silently, validates
the installed executable and version, and restarts Wisp. A verified in-place
update preserves completed setup; a fresh installation still requires the setup
wizard.

No repository credential is embedded. Any response or artifact that cannot be
verified is rejected without starting the installer. This does not make the
unsigned installer Authenticode-signed.

## Offline maintainer audit

`tools/compatibility_audit.py` inspects explicitly supplied executable copies.
It does not start FH6, attach to a running process, or modify its input.

Static verification checks the PE fingerprint, image and section bounds,
threshold data, vtable pointers, and readable getter anchors. Optional discovery
produces bounded candidates for manual review; it never approves a build or
emits a signed runtime contract on its own.

Static analysis cannot prove live local-player identity, car/RPM agreement,
gameplay visibility, protected getter semantics, electric digit/unit behavior,
or assist transitions. A changed contract therefore needs controlled runtime
validation before it can be accepted.

## Failure behavior

Unknown, invalid, ambiguous, or stale state is unavailable. Wisp does not guess
maximum RPM, estimate Native redline, reuse a previous car's assists, or fall
back to a nearby executable build. Native motion playback also resets across
stale samples, hidden lifetimes, car changes, and incompatible sessions.
Diagnostics reports the detected build and the availability of each Native
capability.
