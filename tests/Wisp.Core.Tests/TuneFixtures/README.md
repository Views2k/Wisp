These are bounded numeric excerpts from the existing September 30, 2026 tune
research captures. They contain no process pointers, addresses, game assets,
account data, creator names or share codes.

- `miata.json`: captured car 4197, six forward gears and AWD.
- `editable.json`: captured car 1229, seven forward gears and AWD.
- `rwd.json`: captured car 4200, one forward gear and RWD.
- `miata-reference.json`: 33 expected displayed values from the prior independent
  reference comparison, plus captured float32 bit patterns and the specific
  reference-unit conversion factors.
- `miata-physics.json`: separate gearing/wheel observations from that capture.

Source capture files were checked against the existing research handoff's SHA-256
manifest before projection. The projection keeps all three normalized copies,
the exact numeric bounds/conversion constants and the relevant installed-part
IDs/levels. Pointer-bearing metadata and exploratory candidates are excluded.

The Miata reference validates that capture at displayed precision. Editable-car
confirmation covers selected fields; the RWD fixture has no complete independent
published reference. Mutated fixture cases exercise rejection and availability
branches, not additional live-game coverage. Metric factors here reproduce the
specific reference comparison; the production decoder accepts only the verified
imperial unit selection. No fixture establishes universal car/build support,
gameplay performance or the game's performance-panel predictions.
