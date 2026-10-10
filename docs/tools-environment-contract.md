# Tools environment and crash evidence

The optional `environment` object on schema 1 heartbeat events has its own
`schemaVersion: 1`. Old heartbeat producers remain valid. The service must accept
this extension before a client carrying it is released.

Collection happens on the existing reporting worker, once per five minutes using
a monotonic cache. It does not run on a telemetry, render or dispatcher callback.
The selected Wisp settings are copied on initialization and settings writes.
`collectedAt` describes the machine query; the settings on the heartbeat are the
latest published selection. The service hashes content excluding `collectedAt`.

## Fields

- OS build, logical processor count and rounded physical RAM in GiB.
- Up to four DXGI adapters: PCI vendor/device model identifiers, model text,
  driver version, dedicated memory in MiB and software-adapter flag.
- Up to eight connected outputs: adapter-list index, current width/height,
  refresh rate in milliHz, scaling percent, HDR state and bits per color channel.
- Wisp selections: CPU/GPU rendering, mph/kmh, fixed gauge-style enum, smoothing,
  opacity/scale, game-aware visibility, enabled gauges/lap recording, debug logging
  and Clips enabled. Clips choices include resolution, frame rate, quality, length,
  lossless/HDR/system-audio settings. Invalid optional values are omitted.
- Explicit coverage for physical/displayed frames, game render adapter and actual
  VRR operation: all remain `not-measured` in this contract.
- Optional power context: built-in plan family or custom/unknown, AC/battery/unknown,
  and battery-saver enabled/disabled/unknown. No custom plan GUID or name is sent.
- Optional software presence for six fixed families: Discord, RTSS, Game Bar,
  OBS, SimHub and Overwolf. Values are present/not-observed/unknown; they do not
  indicate whether an overlay is enabled, injected or causing an issue.

Adapter model identifiers describe products, not unique units. No LUID, EDID,
monitor name, serial number, window title, process ID, path or account identifier
crosses this projection. Unsupported model characters produce a fixed label.
The complete object is capped at 8 KiB and independently validated on both sides.

## Measurement boundaries

DXGI `CheckInterfaceSupport(IDXGIDevice)` supplies the driver version. Active
display paths supply a rational refresh rate when available; the fallback is the
integer current display mode. Scaling comes from `GetScaleFactorForMonitor`.

HDR is enabled only when Windows reports advanced color enabled and the output
uses the PQ/Rec.2020 wire color space. Wide-gamut SDR, unavailable APIs and
ambiguous cloned display targets remain unknown. HDR state does not establish
that a particular Wisp or game frame used HDR.

The adapter driving an output is not proof of which GPU renders Forza or Wisp.
These are inventory facts for comparisons, not automatic causal conclusions.

`foregroundState` is a separate heartbeat field: `game`, `wisp`, `other` or
`unknown`. It is one point observation, not a foreground-duration percentage.
The collector never activates or moves a window.

Power facts use Windows `PowerGetActiveScheme` and `GetSystemPowerStatus`.
Built-in plan identity does not prove the plan's settings are unmodified or the
effective CPU performance. Software uses fixed local `GetProcessesByName`
lookups; no full process list enters the projection. Errors and a 500 ms budget
for additional lookups produce unknown values. An in-flight OS lookup is not
claimed cancelable. These fields share the five-minute inventory cache, so they
are context observations rather than interval-aligned causality.

## Crash locations

The existing fourteen reviewed readable method labels remain valid. Other
authored product methods are checked against the actual loaded product
assemblies before projection, then represented as `sha256:` plus a lowercase
64-character SHA-256 digest of UTF-8 `wisp-tools-method-v1\n` followed by the full
compiled method symbol. Exception messages and arbitrary method strings are not
accepted. Existing method/exception count and incident-byte caps remain intact.

The PE-only `tools/Wisp.SymbolCatalog` utility exports the matching method map,
assembly module identities and exact source revision. CI retains this separately
from the installer. A reader must distinguish matching-build resolution from an
unresolved location; it must never invent a name for an unknown digest.
Automatic build identities use the actual app MVID even for private candidates,
so reused private labels cannot bind crash hashes to a different compilation.
Older stored crash reports without an MVID retain their legacy identity and may
remain unresolved. The local human-readable diagnostic label is unchanged.

## Lifecycle outcomes

Game detection is emitted only after a fresh native-provider sample establishes
ready, unsupported-build or access-denied state. This does not define a first
telemetry or successful HUD-display event.

Update helper launch failures, readiness failures and timeouts are terminal
handoff observations. A consumed installed receipt is successful only when its
target version matches the running build; helper readiness alone is never an
installed outcome. Existing installations gaining reporting are not called new
installations, and missing exit reports remain unknown.

Settings loading reports its fixed outcome after reporting initialization.
Malformed/null JSON and read failures report failure; a prepared parsed object
reports success. The existing no-file fallback reports none, not first install
or success: `File.Exists` cannot distinguish every access failure from absence.
