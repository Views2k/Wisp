# Recorder child protocol v1

The managed client launches only its installed, caller-selected recorder EXE,
with `--stdio-protocol`, no shell, and inherited redirected stdin/stdout/stderr.
It does not launch a helper until explicitly opened. This protocol does not make
the existing metadata or synthetic fixture executables production recorders.

Stdin and stdout contain UTF-8 JSON objects, one per LF-terminated line (CRLF is
accepted), at most 16384 bytes excluding LF. No BOM, duplicate members, unknown
members, blank lines, partial final lines, raw diagnostics or per-frame messages.
Maximum JSON depth is 6. The first `config` binds a fresh lowercase GUID-N
`session`. Every command has `v:1`, that `session`, a positive monotonically
increasing signed64-bit `request`, and a `command`. Only one command is pending.

Commands and their additional members:

- `config`: `durationSeconds` (30..300, step30), `height`
  (360/480/720/1080/1440/2160), `frameRate` (30/60), `quality` (10..100),
  `gameAudio:true`, `spoolDirectory` (absolute private new session directory).
  Optional `systemAudio` is a boolean defaulting to `false`: false captures the
  game process tree; true captures system playback excluding the recorder process
  tree. Neither mode directly captures a microphone. The historical `gameAudio`
  field remains required and true in both modes. Optional `borderlessAllowed`
  remains a boolean defaulting to false for compatibility; the Desktop
  Duplication backend does not use it or request WGC borderless permission.
- `start`: `processId` (positive uint32), `window` and `creationFileTime`
  (positive invariant decimal **strings**, at most20 digits). The native helper
  must independently validate the game, window ownership and creation time,
  retain the process handle, and revalidate lifetime. Desktop Duplication captures
  the entire monitor containing this exact game window, including overlays and
  notifications. Capture requires that window to be foreground and its physical
  client bounds to cover the monitor exactly (including borderless fullscreen).
  There is no primary-monitor fallback or automatic audio-source fallback.
- `save`: `clipId` (GUID-N), `destination` (absolute reserved GUID-N `.mp4`
  directly inside the private session workspace that contains `spoolDirectory`).
  The writer uses create-new semantics; this is not the user export folder.
- `stop`: no additional members. Stop capture, drain bounded work and acknowledge;
  the client then closes stdin and waits for process exit.

The managed client creates an exclusively leased workspace at
`%LOCALAPPDATA%/Wisp/ClipBuffer/<session GUID-N>`. Its spool directory is
`<workspace>/.wisp-recorder-<session GUID-N>`. The native helper must recheck path
components/reparse points, create the session directory without reusing an
existing directory, and preserve recoverable media on failure. Buffer files
contain encoded packets, not raw frames. Clean shutdown discards owned rolling
data; bounded startup cleanup only removes proven owned, inactive data using
leases and file identities. Failed publication preserves completed private media.
After native finalization, managed code publishes to the reserved private Wisp
library path and verifies the held destination identity before indexing it.
The selected user export folder is written only by an explicit export.

Each response has `v`, `session`, matching positive `request`, `type:"result"`,
`ok` and `reason`. A successful non-save response has no other members. A failed
response has no media. A successful save also has `clipId`, `fileBytes`, `width`,
`height`, `frameRate`, `start100ns`, `end100ns`, `hasAudio` (boolean). Times are actual
ordered media bounds in100ns units, not the requested nominal duration. Send
success only after mux finalization and every output file handle is closed. The
library still verifies its reservation, path, size and publication identity
before making a playable entry.

Asynchronous state has `v`, `session`, `request:0`, `type:"state"`, `state` and
`reason`. States are `waiting`, `buffering`, `saving`, `paused`, `reconnecting`,
`stopped`, `error`. `buffering` requires usable encoded keyframe and, when enabled,
audio coverage; config/start success does not imply frame delivery.

`paused` is valid only with `window_minimized`, `focus_lost` or
`fullscreen_required`. `reconnecting` is valid only with `target_exited`,
`target_changed`, `window_closed`, `window_resized`, `capture_reconnecting`,
`encoder_reconnecting`, `audio_reconnecting` or `scheduler_late`. These terminal
states are emitted after successful cleanup. Managed recovery waits for eligible
target observations and starts a fresh A/V epoch with a new rolling buffer;
already saved clips remain. A `buffering`/`capture_stale` state instead reports
repetition of the latest frame while the same target remains eligible.

No raw paths, process/window identity or exception text belong in output. Stderr
has a 16384-byte total bound. The client retains only the first valid, allowlisted
failure-diagnostic JSON record of at most 4096 bytes for Copy error details;
other stderr text is discarded and never shown as a raw error message.

Fixed reasons (the managed list is authoritative for v1):
`none`, `waiting_for_game`, `target_exited`, `target_changed`, `window_closed`,
`window_minimized`, `window_resized`, `focus_lost`, `fullscreen_required`, `unsupported_os`,
`unsupported_gpu`, `unsupported_format`, `capture_failed`, `encoder_failed`,
`audio_failed`, `audio_capture_failed`, `audio_unavailable`, `buffer_full`, `no_keyframe`, `not_ready`, `save_in_progress`,
`storage_failed`, `mux_failed`, `protocol_error`, `cancelled`, `stopped`,
`parent_closed`, `capture_stale`, `capture_reconnecting`, `encoder_reconnecting`,
`audio_reconnecting`, `scheduler_late`, `cleanup_failed`.
Successful results use `none`, except a valid silent save uses
`hasAudio:false,reason:"audio_unavailable"`; the UI must explain that selected audio
was unavailable. An audible save uses `hasAudio:true,reason:"none"`. Unsupported
loopback never permits switching the selected source or microphone fallback. Failed results use
a different fixed reason.

The native helper must terminate on stdin EOF, including when the parent dies.
No helper-created child processes. Commands have managed deadlines: config/start
15seconds, save5minutes, stop5seconds. The save bound permits a five-minute2160p
clip to finish on slower storage; cancellation and stopping do not wait for that
deadline. Timeout, cancellation, malformed protocol,
unexpected EOF or stderr overflow fail pending work and terminate only the exact
owned helper. The transport does not retry; the managed service owns bounded,
visible recovery from supported interruption reasons. Shutdown allows5seconds after
stdin closure, then terminates that child and waits up to2seconds. This transport
does not establish full-recorder performance, playable A/V output or game support.

Managed-only failure codes (not accepted from child JSON): `helper_start_failed`,
`helper_exited`, `helper_timeout`, `helper_shutdown_failed`,
`buffer_storage_unavailable`, `buffer_storage_full`, `clip_storage_full`,
`clip_publish_failed`. These contain no raw
operating-system error text or paths. A caller must not treat an unconfirmed
shutdown as successful cleanup.
