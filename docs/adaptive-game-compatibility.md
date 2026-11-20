# Adaptive Forza reader compatibility

Known compatibility packs keep their existing admission and validation path. When an official game executable has no matching pack, Wisp can locate unchanged readers at new image addresses. A version number or executable hash change alone is no longer sufficient to reject those readers.

## Admission and accuracy

- Steam admission requires Windows to verify the executable signature and its Playground Games publisher. Xbox admission requires the existing OS package and executable-identity checks.
- Embedded profiles describe independently verified reference readers. They are application resources, not downloaded or user-supplied compatibility packs.
- Discovery permits decoded address operands to relocate. Reader opcodes, member offsets, constants, complete guarded ranges, table relationships and ownership must still match.
- Discovered references must agree with each other, including references into the middle of a guarded function. Ambiguous matches are rejected.
- The reader evidence is read again before admission and when a cached result is reused. Process identity and catalog generation are checked around admission.
- Existing runtime checks still establish the local car, object ownership, coherent values, telemetry agreement and Tune data integrity. A structurally discovered layout does not make arbitrary runtime values valid.
- A rejected or revoked exact pack cannot be bypassed with adaptive discovery. Runtime-validated Tune snapshots record their actual game identity and a distinct verification method.

## Independent capabilities

Core HUD compatibility is required first. Tune and direct native gauge readers are then validated separately. An incompatible optional reader does not discard the verified core. Failed optional discovery rolls back its addresses, relationships and recorded evidence.

Both platforms require typed code witnesses for all 18 direct-gauge fields. Steam uses the independently verified common field-reader code in an isolated Store reference namespace: complete initializer and getter code, exact property names, member offsets, type calls and the complete type function must all match. Only a verified single jump alias is accepted. This does not copy Store addresses into a Steam reader.

The field reader code carries the game's thread-local storage slot offset as an immediate operand (`mov edx, imm32` after the `gs:[0x58]` access). That offset is recompiled per build: 0x420 through 6.461.691.0 and 3.461.691.0, 0x42C from 6.483.970.0 and 3.483.970.0, where it rejected the direct gauge on every PC. The type function template and the 18 initializer templates declare it as a `threadLocalSlot` operand: it is masked like an address operand, it has no target, and every matched reader must carry the same 4-byte-aligned value below 64 KB. Getters do not contain it, and their member displacements are still compared byte for byte.

A reference that two templates share may reach its target through a single 5-byte jump thunk on one side and directly on the other (Store 3.483.970.0 routes one Tune guard's call to a helper that the gauge templates call directly through such a thunk). Both shared-reference checks accept exactly that case and keep the helper itself; the thunk bytes join the proof. Any other disagreement between shared references is still rejected.

Steam's secondary HUD table is linked to the already verified main HUD table through the same complete-object RTTI type and hierarchy. Its subobject offset, full inheritance topology and slot code must still match, and the candidate must be unique. Table spacing and numeric RTTI labels are not treated as stable identities. This relationship was checked in both retained versions of both platforms.

## Drift angle and scoring guidance

Drift angle is calculated from Data Out. Custom targets and measured angle remain available when a Drift Zone scoring profile cannot be verified. The gauge then says `ANGLE ONLY` and withholds the bonus percentage and colored scoring guide.

Drift Zone bonus rules have a separate compatibility boundary. Successful adaptive HUD or Tune validation does not verify those rules. The loaded curve values must be checked before a build receives a bonus profile; unchanged code alone cannot establish unchanged configuration. The drift renderer itself reads no game memory: it asks `DriftZoneCurveCheck` for the profile on each frame and gets a stored answer.

Regression tests require the newest bundled Steam and Xbox builds to have drift guidance, in addition to checking the captured identities. This prevents a future bundled compatibility update from silently omitting drift again. Changed identities and unknown adaptive builds still receive no unverified bonus claims from the table.

### Curve check for builds outside the table

A build that is accepted but not in the scoring table gets the bonus guide only after Wisp has read the running game and found the curve. Until then the gauge says `ANGLE ONLY`.

- `DriftZoneCurveCheck` starts the read the first time the gauge needs a profile in Drift Zone mode while the car is driving. One read runs at a time, on a thread at the lowest priority.
- `DriftZoneCurveScan` goes through the game's committed, private, writable memory with the read-only handle the HUD already holds. It looks for the float32 saturation angle and requires the minimum and maximum angles and both multipliers as float32 within 128 bytes, in one arrangement, at least eight times. It then waits one second and reads every entry again. Both checked builds hold 20 such entries.
- The read is paced: after each 4 MiB it waits three times as long as that chunk took. It stops after 90 seconds or 32 GiB and then reports no result.
- The result is kept per exact build identity in `drift-zone-checks.json` in Wisp's local data folder, so a build is read once per PC. A verified build is read again after 14 days. A build without the curve is tried again after 2 minutes, 10 minutes and then hourly.
- Builds in the table are read once as well. Their guidance does not depend on it; the result shows whether the check agrees with the table before the next game update needs it.
- Every result is reported once per run as a `drift` `check` observation: `success`, `unsupported` (curve not found), `unknown` (entries changed between the two reads), `timeout` or `failure`.
- The `drift-zone-check` feature control pauses the read. While it is paused, a build outside the table stays on `ANGLE ONLY`.

Limits: the handle has only the limited query right, so the read cannot tell which pages are currently in memory and reads the others too. The check does not compare scoring code, and it cannot see a single zone whose curve differs while enough others still match.

On October 6, bounded read-only checks of Steam 6.461.691.0 and Xbox 3.461.691.0 each found 20 stable typed scoring configurations. All observed instances matched the existing curve: 10-degree minimum, 110-degree maximum, float32 59.4-degree saturation, and multipliers 30 to 40. The checks completed in 4.77 and 4.56 seconds. Complete captured instruction ranges matched the retained scoring references on both platforms (19 Steam ranges and 21 Xbox ranges), with known scoring relationships checked separately. This qualifies the existing curve for these exact builds; it is not automatic admission of future profiles.

On November 19, a read-only scan of the running game checked Steam 6.483.970.0 and Xbox 3.483.970.0. Each held 20 loaded scoring entries, the same count as October, and every entry held the existing curve: 10-degree minimum, 110-degree maximum, float32 59.4-degree saturation, and multipliers 30 to 40. The entries were unchanged on a second read 1.5 seconds later, their neighbouring fields were identical on both platforms, and no 59.4 value was found without the other four beside it. The scans read all resident private writable memory (7.5 GiB on Steam, 4.9 GiB on Xbox) in 1.1 seconds each. This qualifies the existing curve for these exact builds. The scoring code was not compared with the October references for these two builds.

These were configuration/code checks, not an active scoring or displayed-gauge test. Sampling was non-atomic and bounded to 2 GiB; it did not enumerate every allocation, link an active component to the local car, or compare simultaneous native and Data Out angles. Constant references were checked against retained image evidence. The diagnostic and its one-time memory search are not part of the shipped application.

## Runtime cost and lifecycle

Discovery runs on attachment workers, never on HUD ticks. HUD and Tune share one bounded discovery per process identity and catalog generation. The snapshot is limited to 256 MiB, the retained evidence to 4 MiB, and discovery to 30 seconds, including cancellable contention for the discovery lock. Only initialized writable image bytes are scanned.

Successful results retain addresses and small evidence hashes, not a full image snapshot. Structural rejection is cached for that process generation. Transient read or publisher-verification failures have a one-minute retry delay. A temporarily unreadable optional reader keeps the verified core available but expires that partial result so a later attachment can retry. Cancellation propagates through both attachment workers, including a cancellation arriving during the final proof read.

## Evidence and remaining validation

Reference profiles were generated from Steam 6.440.853.0 and Store 3.440.853.0. Production discovery was exercised against retained images from Steam 6.461.691.0 and Store 3.461.691.0 without supplying the new address maps to discovery. The new maps were used only as the result oracle.

- Steam: all 115 core, Tune and direct-gauge roles matched the independently established new map in a replay combining the retained image with 8,206 bytes of accepted live field evidence. Only the captured absolute type-slot pointer was rebased; code and property names were unchanged.
- Store: all 126 roles matched, including all 18 typed gauge-field witnesses.
- Deliberately changed core code was rejected on both images. A Tune-only change left core and gauge support available on both. A gauge-only change left core and Tune available on both.
- Targeted reader, publisher, capability isolation, cache, cancellation and Tune decoder tests passed. These tests do not establish displayed HUD smoothness or behavior on an unseen future game build.

The Steam fallback also passed a live, read-only comparison on 6.461.691.0 with a combustion car in the open world: the actual adaptive factory admitted the game, core HUD results matched the known reader, and all 37 Tune fields matched. Discovery and both Tune reads completed in 1.8 seconds. Xbox/Store 3.461.691.0 passed the same comparison, including native gauge field validation, in 2.0 seconds. The initial Xbox attempt stopped when gameplay was reported hidden; the next bounded attempt passed. These checks did not exercise rendered HUD motion or independently compare against UDP telemetry.

An additional live Steam check verified all 18 named fields, full getter/initializer code and their type relationships, then reread the accepted proof for stability. It completed in 0.36 seconds. The final integrated Steam table/field path was checked against the saved evidence described above; it was not a rendered-HUD test.

The full application/installer matrix remains a separate validation step. This is not a guarantee that every future game build will work: changed reader semantics, inaccessible memory, invalid game provenance or ambiguous matches must still fail safely.
