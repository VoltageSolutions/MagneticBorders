# Icon

The icon is a horseshoe magnet turned 45 degrees with its opening toward the lower left, a red pole and a blue pole, and two yellow-orange sparks. It follows Microsoft's app icon guidance: simple forms, subtle gradients, one light from the top left. `src/common/assets/icon.svg` is the source. Never edit a copy.

The magnet stands for edges that stick together. The two sparks, one vertical and one horizontal, stand for the two directions a shared border can be dragged.

## Sources and files

| ID | Rule |
|---|---|
| ICON-001 | `src/common/assets/icon.svg` must be the full icon for 40 to 256 px, on a transparent background, with `viewBox="49 24.5 176 176"`. |
| ICON-002 | `src/common/assets/icon-small.svg` must be the magnet alone for 16 to 32 px, on a transparent background, with `viewBox="63.75 26.25 160 160"`. |
| ICON-003 | `icon.ico` must contain 16, 20, 24, and 32 px frames exported from `icon-small.svg`, and 40, 48, 64, and 256 px frames exported from `icon.svg`. |
| ICON-004 | The engines must embed `icon.ico` as their exe icon and their notification area icon. The settings app must use it as its app icon. |
| ICON-005 | Nobody may edit a copy of the icon. Changes go to the SVG sources, then the `.ico` is regenerated. |

## Shape

| ID | Rule |
|---|---|
| ICON-010 | The magnet must be drawn opening-down, then rotated 45 degrees clockwise about (150, 100), so the opening faces down-left. |
| ICON-011 | The magnet body must be an arch with outer radius 63 and inner radius 29 (a 34 wide band), with legs 20 long, as a filled outline. |
| ICON-012 | The poles must be 34 by 42 rectangles at the leg ends, separated from the body by a 2 px gap. |
| ICON-013 | Every pole corner and the body's leg-end corners must have a 7 px radius. The arch itself is round. |

## Color

| ID | Rule |
|---|---|
| ICON-020 | The body must use a linear gradient from `#B4BAC2` to `#8A919B`. |
| ICON-021 | The north (red) pole must use a linear gradient from `#FF6B6B` to `#D62F3A`. |
| ICON-022 | The south (blue) pole must use a linear gradient from `#5AA9FF` to `#1F5FD6`. |
| ICON-023 | Every gradient must run at 120 degrees measured on the screen, not rotated with the magnet: light at the top left, dark at the bottom right. Each gradient must span its own shape. |
| ICON-024 | The sparks must be flat `#FFB020`, with no outline and no gradient. |

## Sparks

| ID | Rule |
|---|---|
| ICON-030 | Each spark must be a lightning bolt of about 44 by 20 px. |
| ICON-031 | A bolt must be a zigzag with two short steps across the middle. Both tips must sit on one straight line (vertical for one bolt, horizontal for the other). The bolt must be 180 degrees rotationally symmetric. |
| ICON-032 | The bolts must sit just outside the opening. The two must be mirror images across the magnet's 45 degree axis (the line x + y = 250): the vertical bolt below the blue pole, the horizontal bolt left of the red pole. |

## What the icon must not have

| ID | Rule |
|---|---|
| ICON-040 | The icon must not have shine or reflection lines, outlines, drop shadows, or text. |

## Small sizes

| ID | Rule |
|---|---|
| ICON-050 | Below 40 px the sparks must be dropped, because they turn into noise. |
| ICON-051 | Both variants must stay recognizable on light and dark taskbars. |
