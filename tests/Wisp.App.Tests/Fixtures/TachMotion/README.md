# Saved moving RPM fixture

`moving-rpm-128.json` contains 128 consecutive, unresampled samples from the saved telemetry audit. Only `receiveMilliseconds`, `gameMilliseconds`, and `rpm` remain. Both clocks start at zero independently; RPM values and receive ordering are unchanged.

Source: `telemetry-audit-5601.json`

Source capture SHA-256: `A859448F5C5491F388E5E9E39BA73516348E32BBE57F2C33463B2C91F9C91658`

Selection: zero-based source rows 1881-2008, beginning 21107.206 ms after the trace's first receive timestamp. Every selected sample reports at least 5 m/s, with unchanged vehicle identity and no timestamp reversal. Identity, speed, absolute UTC, and endpoint information are not included in the fixture.

The fixture spans 1754.1518 ms of receive time and 1765 ms of game time. RPM ranges from 2788.1716 to 6228.4604. It includes 14 adjacent equal-game-time pairs with differing RPM; the largest absolute change is 557.5971 RPM. Receive time is strictly increasing; game time is nondecreasing with intentional duplicates.

Full-source aggregate: 3,660 samples over 44.9565416 seconds. Of 3,659 intervals, 779 (21.29%) have equal game timestamps and differing RPM; 703 are moving pairs. Receive rate is approximately 81.39 samples/s versus 64.06 advancing game timestamps/s. Nonzero game steps are 15/16 ms: every eight total 125 ms. The game/receive elapsed-time ratio is 1.000967 overall.

This is regression-test input for preserving distinct samples and timestamp quantization, not proof of native visual or motion parity. The recorded receive clock was UTC, not a monotonic counter; subtracting its origin does not change that limitation.
