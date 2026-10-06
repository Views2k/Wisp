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

Steam's secondary HUD table is linked to the already verified main HUD table through the same complete-object RTTI type and hierarchy. Its subobject offset, full inheritance topology and slot code must still match, and the candidate must be unique. Table spacing and numeric RTTI labels are not treated as stable identities. This relationship was checked in both retained versions of both platforms.

## Drift angle and scoring guidance

Drift angle is calculated from Data Out. Custom targets and measured angle remain available when a Drift Zone scoring profile cannot be verified. The gauge then says `ANGLE ONLY` and withholds the bonus percentage and colored scoring guide.

Drift Zone bonus rules have a separate compatibility boundary. Successful adaptive HUD or Tune validation does not verify those rules. The scoring code and loaded curve values must be checked separately before a build receives a bonus profile; unchanged code alone cannot establish unchanged configuration. No new game-memory scans or reads have been added to the drift renderer.

Regression tests require the newest bundled Steam and Xbox builds to have drift guidance, in addition to checking the captured identities. This prevents a future bundled compatibility update from silently omitting drift again. Changed identities and unknown adaptive builds still receive no unverified bonus claims. Automatic discovery of future Drift Zone scoring profiles is not implemented.

On October 6, bounded read-only checks of Steam 6.461.691.0 and Xbox 3.461.691.0 each found 20 stable typed scoring configurations. All observed instances matched the existing curve: 10-degree minimum, 110-degree maximum, float32 59.4-degree saturation, and multipliers 30 to 40. The checks completed in 4.77 and 4.56 seconds. Complete captured instruction ranges matched the retained scoring references on both platforms (19 Steam ranges and 21 Xbox ranges), with known scoring relationships checked separately. This qualifies the existing curve for these exact builds; it is not automatic admission of future profiles.

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
