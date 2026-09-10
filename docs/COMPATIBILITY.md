# Compatibility and Update Safety

## Current support

Wisp bundles separate Native HUD compatibility contracts for:

- Steam FH6 build `6.440.853.0`, identified by its recorded executable fingerprint;
- Steam FH6 build `6.430.771.0`, identified by its recorded executable fingerprint;
- Xbox app / Microsoft Store Windows PC build `3.440.853.0`, identified by its
  Store package and bounded loaded-image checks;
- Xbox app / Microsoft Store Windows PC build `3.430.771.0`, identified by its
  Store package and bounded loaded-image checks.

Support is build-specific; Wisp does not reuse one storefront's contract for
another binary with a similar version. Wisp runs alongside a local Windows
installation, not on Xbox consoles or inside cloud gaming.

Data Out speed and G-force use the local 324-byte UDP packet. Exact Native
redline, assist, and electric-gauge state use a separate guarded read-only path
because those values are not fully represented in Data Out. Data Out reception,
calculations, and dashboard diagnostics do not depend on that provider. Driving
overlays still require validated Native gameplay visibility so they cannot leak
into menus or cutscenes; when visibility is unavailable, the overlays fail
closed even if valid UDP data continues to arrive.

Wisp treats an unknown executable as incompatible instead of applying offsets
from a different build.

## New cars, tunes, and content

Wisp has no car whitelist or redline database. A positive Data Out car ID is
matched to the unique current local-player source, and redline and tach maximum
come from that vehicle's live model.

A new car or tune that uses the supported runtime schema does not need a new
lookup row. A content update that also changes the validated game build identity
still requires a reviewed compatibility contract. Changed packet fields, powertrain
semantics, or renderer behavior can require an application update rather than a
data-only contract.

Car changes, activity transitions, maximum-RPM changes, and telemetry rewinds
invalidate in-flight Native state. A return to an earlier car cannot publish a
reading from the previous session.

## Read-only Native provider

Before accepting Native state, Wisp validates:

- the storefront-specific build identity described below;
- compatibility schema and bounded module-relative addresses;
- process generation, executable path, and module identity;
- vtable guards and field alignment;
- a unique local-player source;
- Data Out car identity, current RPM, and maximum RPM agreement.

Steam retains its executable version, byte length, SHA-256, and image-size checks.
The Store path checks the exact Windows package name and version, Store origin,
installation path, PE machine type, timestamp, image size, and hashes of bounded
read-only executable regions. When Windows reports different paths for the same
Store executable, attribute-only file handles must confirm the same file identity
and metadata. These checks do not claim a full-file hash of the protected Store
executable and do not require elevated access or permission changes.

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

Wisp 1.1.2 configures a release-owned ECDSA P-256 public key and the fixed HTTPS
endpoint `https://wispoverlay.com/compatibility/latest.json`. Checks run in the
background at startup and once per day while Wisp stays open; Diagnostics also
offers a manual check and signed-file import. Requests have bounded sizes and
timeouts, no redirects, and no telemetry upload. A failed download or signature
check leaves the accepted catalog unchanged. Bundled maps work without a
network connection or an available publisher endpoint.

The signed payload can contain one legacy contract (format 1) or up to eight
contracts (format 2). A bundle can cover separate Steam and Store builds. Every
entry must pass the same schema and identity rules, and every Store attachment
still requires Windows package provenance and loaded-image guards. Wisp never
uses a Steam contract for a Store executable.

Bundle installation validates every entry before committing one acceptance
ledger. Revision floors prevent replacement with an older accepted map. A
failed write cannot publish a partially installed bundle. Previously accepted
maps are reverified from the local cache at startup and remain usable offline,
including after the download envelope's original expiry. Older embedded maps
remain available for installations that have not updated Forza.

This channel permits reviewed address-map updates without reinstalling Wisp.
It cannot automatically approve an unknown native layout. A game update that
changes field semantics, ownership rules, or the supported reader schema still
requires code review and may require a new application release.

This small pinned-key protocol is not a complete software-update framework. It
does not claim protection from a hostile local administrator, application
replacement, local cache rollback by the same user, or system-clock tampering.

## Application updates

The application updater is separate from compatibility contracts. Availability
checks run whenever Wisp opens and every 24 hours while it remains running,
including while waiting in the tray. They are enabled by default; turn off
**Automatically check on open and daily** in **Extras** to disable them.
**Check for updates** also allows a manual check. The client uses GitHub's
anonymous latest-release endpoint and requires a non-draft, non-prerelease,
immutable release. Exactly one uploaded
`Wisp-Setup-<version>.exe` asset provides the numeric version, byte length, and
GitHub SHA-256 digest. One-, two-, and three-part numeric versions normalize to
`X.Y.Z`; numeric tags must agree with the installer. Stable release titles are
display text, never version-ordering evidence.

Use canonical tags such as `v1.1.0` and filenames such as `Wisp-Setup-1.1.0.exe`
when publishing for clients older than Wisp 1.1. Those clients cannot discover
short-tag releases, even if they skipped the release that added support.

Wisp shows the release summary and asks for confirmation before downloading and
installing the update. The initial download URL and every redirect must remain
on the allowlisted GitHub HTTPS release path. After confirmation, Wisp downloads
the installer and verifies its length and digest. A staged helper repeats the
artifact and process checks, waits for Wisp to exit, runs the current-user Inno
installer silently, validates
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

### Publishing a reviewed map

`tools/Sign-CompatibilityBundle.ps1` signs reviewed JSON contracts locally with
the release owner's Windows-user-protected key. The private key stays outside
the repository, installer, and website. Its DPAPI protection depends on the
owning Windows profile; copying that file to a different machine is not a
portable key backup. Never replace the established key during a routine map
update. Changing the pinned key requires a reviewed application release.

After static and live validation, sign one bundle containing the builds being
maintained. Increment the revision of every included previously signed map
when creating a different payload, even if its address data is unchanged:
timestamps and the other bundle members are part of the signed payload too.
Do not reuse a revision from a different payload. A client with a newer embedded
map keeps that map when processing a bundle; cached revision floors still apply.

```[WINDOWS POWERSHELL]
./tools/Sign-CompatibilityBundle.ps1 -KeyFile $protectedPublisherKeyPath -Pack $reviewedPackPaths -Output ./outputs/compatibility/latest.json -Reviewed
```

Before publication, verify that exact file through Wisp's pinned-key catalog in
an isolated cache, test a corrupted copy is rejected, and repeat acceptance from
the cache with networking unavailable. Publish only the resulting signed JSON
at the configured endpoint after approval. Serve JSON without a redirect or
content encoding, with a short cache lifetime, and retain the exact published
bytes for auditing. Publishing an HTML error page or an unsigned pack never
changes the accepted catalog.

## Failure behavior

Unknown, invalid, ambiguous, or stale state is unavailable. Wisp does not guess
maximum RPM, estimate Native redline, reuse a previous car's assists, or fall
back to a nearby executable build. Native motion playback also resets across
stale samples, hidden lifetimes, car changes, and incompatible sessions.
Diagnostics reports the detected build and the availability of each Native
capability.
