# Fixtures

A fixture is a tiny `.layout` text file that describes windows, one drag, and the expected result. Every engine's `fixture-runner` runs the same files in `tests/fixtures/`. They keep three languages in agreement.

## Format

```text
# comment
tolerance 12                     # optional, default 12
workarea 0,0 3440x1440           # optional, default -100000,-100000 200000x200000
win A 0,0 1272x1440              # id x,y WxH  [min WxH]  (default min 1x1)
win B 1280,0 1272x716 min 400x200
drag A right +100                # id edge signed-delta
expect A 0,0 1372x1440
expect B 1380,0 1172x716
expect clamp 1372                # optional
```

| Line | Meaning |
|---|---|
| `tolerance N` | The Magnetic coupling tolerance in pixels. |
| `workarea x,y WxH` | The monitor work area. Edges at its boundary never form seams. |
| `win id x,y WxH [min WxH]` | A window by id, with its visual frame and an optional minimum size. |
| `drag id edge delta` | Drag one edge of one window. `edge` is `left`, `right`, `top` or `bottom`. `delta` is signed and applies to that edge's coordinate. |
| `expect id x,y WxH` | The expected final frame of a window. |
| `expect clamp N` | The expected clamped edge position. |

Rules for the runner:

1. The snapshot is the start frames.
2. Build the seam from the dragged window and edge. The new edge position is the start position plus the delta.
3. Propagate. The final dragged edge is the clamped position, or the unclamped one when there is no seam.
4. Apply the moves.
5. Every window not named in an `expect` line must be unchanged.

## Example: a clamp

```text
# Neighbour has a 1600px minimum: seam stops at 1840, dragged window snaps back to it
workarea 0,0 3440x1440
win A 0,0 1720x1440
win B 1720,0 1720x1440 min 1600x200
drag A right +300
expect A 0,0 1840x1440
expect B 1840,0 1600x1440
expect clamp 1840
```

## Example: something that must not couple

```text
# 40px gap is wider than the 12px tolerance: not coupled
workarea 0,0 3440x1440
win A 0,0 1700x1440
win B 1740,0 1700x1440
drag A right +20
expect A 0,0 1720x1440
```

B is not named in an `expect` line, so the runner checks that it did not move.

## The fixture set

| File | What it checks |
|---|---|
| `01-two-side-by-side` | Two windows, no gap. |
| `02-fancyzones-gaps` | Three columns, 8 px gaps, a split middle column. |
| `03-drag-left-edge-column` | Dragging a left edge. Stacked windows move together. |
| `04-min-width-clamp` | A neighbor's minimum stops the seam. |
| `05-screen-edge-not-a-seam` | The work-area boundary is never a seam. |
| `06-gap-too-wide` | A gap wider than the tolerance does not couple. |
| `07-horizontal-seam` | A top-to-bottom seam. |
| `08-quadrants` | Four windows on one center line. |
| `09-tolerance-override` | A wider tolerance keeps a 20 px gap. |

## Running them

Each engine has a `fixture-runner`:

```powershell
fixture-runner tests/fixtures              # PASS or FAIL per file, exit 1 on any failure
fixture-runner --print tests/fixtures/02-fancyzones-gaps.layout
```

`--print` writes the final frames as `id x,y WxH`, sorted by id. The `compare` target of the build watcher diffs the three engines' output.

## Adding a fixture

1. Pick the next number and a short name: `10-your-case.layout`.
2. Add a comment that says what it checks.
3. Write the layout, the drag and the expected frames.
4. Run all three runners. A fixture that fails in one engine is a parity bug.

Never change a fixture's expected output without updating the matching [spec](magnetic-coupling.md) rule in the same change.
