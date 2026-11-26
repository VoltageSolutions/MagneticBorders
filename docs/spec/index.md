# Spec

The specs state, in testable rules, how Magnetic Borders must behave. Every rule has an ID such as `RESIZE-012`. Tests, code comments, and issues cite those IDs. If the app and a spec disagree, one of them has a bug, and the fix lands in the same change.

## How a spec reads

A spec describes one part of the product in plain language, then lists its rules in a table. The rules stay true until a spec changes.

A spec changes in the same change that changes the behavior. A behavior change lands in all three engines (see [Parity](parity.md)).

## Words with a fixed meaning

| Word | Meaning |
|---|---|
| **must** | The rule is required. |
| **should** | The rule is expected unless there is a good reason to do otherwise. |
| **may** | The rule is optional. |

## IDs

An ID is a prefix, a dash, and a three-digit number, for example `CFG-007`. Numbers are never reused. If a rule is deleted, its number stays retired. New rules take the next free number on their page.

The user-facing names are [Magnetic coupling](magnetic-coupling.md) and Magnetic attraction. The settings keys keep the older `glue` and `snap` names so saved files keep working, and the IDs use the prefix `RESIZE` (see [D-014](decisions.md)).

## The specs

| Spec | Prefix | What it covers |
|---|---|---|
| [Magnetic coupling](magnetic-coupling.md) | `RESIZE` | Eligibility, classifying a drag, seams, propagation, minimums, snap-back, timers. |
| [Background app](engine.md) | `ENG` | The background app: notification area icon, menu, single instance, logs, start at login. |
| [Contract](contract.md) | `CFG`, `PIPE` | `settings.json` and the named pipe protocol shared by all four apps. |
| [Settings app](settings-app.md) | `UI` | The flyout and the full settings window. |
| [Icon](icon.md) | `ICON` | Shape, colors, gradients, and sizes of the icon. |
| [Localization](localization.md) | `L10N` | Where user-facing text lives and how it is checked. |
| [Parity](parity.md) | `PAR` | Three engines, identical behavior, shared fixtures. |
| [Fixtures](fixtures.md) | | The `.layout` test format and the fixture set. |
| [Design decisions](decisions.md) | `D-` | Why things are the way they are. |

## Citing a rule

In a test name or comment, write the ID, for example `// RESIZE-015`. In a pull request, list the IDs that changed. When a behavior has no rule yet, add the rule first.
