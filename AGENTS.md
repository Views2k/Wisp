# Wisp change delivery

- Small change, small process: inspect, edit, run affected checks, then use the authorized protected PR path. No new tests that pin wording or layout, and no verification paperwork for UI changes.
- Every merge-gating job has a 10-minute timeout. Build and keep fast behavioral guards for settings/data, security, telemetry, HUD/needles and update safety; add tests for affected behavior. Diagnose a slow stage instead of waiting or repeating it blindly.
- Broad UI/layout, full-race simulation and allocation/performance suites run nightly. Nightly failures are reported and do not block merges.
- Do not repeat the full PR suite on main. Installer construction, integrity, updater guards and install/upgrade/uninstall lifecycle checks run for releases, separately from the PR gate.
- Private test installers precede publication when requested. They retain setup skipping, distinct build identity and source/hash provenance. Never publish private metadata or setup bypasses.
- Do not download third-party files to the owner's PC without explicit permission and malware scanning. Reuse existing local tools and dependencies.
