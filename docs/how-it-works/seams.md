# Seams

A **seam** is the set of window edges that lie on one line and should move together. It is the core idea of the app, and it is plain geometry. It needs no Windows calls, so it lives in the `core` layer and all three builds share the same test cases.

## Building a seam

Say you grab the right edge of window A, at x = X. The seam is the vertical line at x = X. For a top or bottom edge, it is a horizontal line instead.

1. **Screen edges are never seams.** If the edge is within the detection zone of the edge of the work area, stop. There is no seam.
2. **Note how far the seam reaches.** Call it the span. It starts as the dragged window's extent along the line: top to bottom for a vertical seam.
3. **Collect candidates.** Look at every other eligible window with an edge near X, within the detection zone.
    - A **same-side** window has the same kind of edge there (its right edge). It moves along with the dragged edge.
    - An **opposite** window has the facing edge there (its left edge). It gives up or takes space.
    - If a window matches both, it counts as opposite.
4. **Keep only those that touch the span.** A candidate joins if the gap between its extent and the span is no more than the detection zone. Overlap counts as gap 0.
5. **Repeat.** Each joined window widens the span. That can pull in more candidates, so MB loops until nothing changes. This is how a stack of three windows on one side all join.
6. **Remember each offset.** For every member, MB stores the distance from its edge to the dragged edge. Opposite members keep their gap because of this. It is how FancyZones spacing survives.

If no member joined, there is no seam, and Magnetic Borders leaves the drag alone.

A corner drag grabs one horizontal and one vertical edge. MB builds two independent seams.

## Moving the seam

On every location change, the dragged edge has a new position P. Each member's edge goes to `P + offset`. Same-side members move the same kind of edge. Opposite members move the facing edge.

Before applying, MB **clamps** P so that no member drops below its minimum width (or height for a horizontal seam). If the limits cross, nothing moves.

### Learning minimums

Magnetic Borders cannot read another program's minimum window size from outside. So every window starts with the system minimum. After each batch, MB reads the real sizes back. If a window came out bigger than requested, it refused. MB records the size it ended up with as that window's minimum until Magnetic Borders restarts.

Windows lets the dragged window keep moving past the clamp. MB cannot stop that from outside. The neighbors stop at their limits, and on release the dragged window snaps back to the clamp.

## A worked example

A 3-column FancyZones layout on a 3440 by 1440 monitor has 8 pixel gaps. The middle column is split into two rows. This is fixture `02-fancyzones-gaps`.

```text
A  x 0..1272     full height
B  x 1280..2552  y 0..716
C  x 1280..2552  y 724..1440
D  x 2560..3440  full height
```

A's right edge is dragged 100 pixels to the right. The detection zone is 12.

- The dragged edge is at X = 1272. The span is y 0 to 1440.
- B's left edge is at 1280, which is 8 pixels away. B is an opposite member with offset +8, and its extent overlaps the span.
- C's left edge is also at 1280. C is an opposite member with offset +8.
- D's left edge is at 2560. That is far from X, so D is not part of the seam.

The new position is 1372. A's right edge moves there. B and C get a left edge of 1372 + 8 = 1380. Their right edges do not move, so they shrink from 1272 to 1172 pixels wide. D does not move. The 8 pixel gap is intact.

```text
A  0,0 1372x1440
B  1380,0 1172x716
C  1380,724 1172x716
```

### With a minimum

In fixture `04-min-width-clamp`, B is 1720 wide and cannot go below 1600. A is also 1720 wide, and its right edge is dragged 300 pixels. B can give up only 120 pixels, so the clamp is 1840. B stops at 1840, 1600 wide. On release, A snaps back to a right edge of 1840.

## In the code

The functions are `build_seam` and `propagate` in the `core` layer of each build. `SeamBuilder.Build` and `SeamBuilder.Propagate` are the C# names. See the [code tour](code-tour/cpp.md) and the [fixtures](../spec/fixtures.md).
