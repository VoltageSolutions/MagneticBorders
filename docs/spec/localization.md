# Localization

Every user-facing string belongs in a resource file so it can be translated. The settings app uses `.resw` files with `x:Uid`. The engines, which have only a handful of strings, use resource tables. English comes first, and a pseudo-locale build catches hard-coded or truncated text before real translations arrive.

## Where strings live

| ID | Rule |
|---|---|
| L10N-001 | New user-facing text must be added to a resource file, never hard-coded. |
| L10N-002 | The settings app must keep strings in `Strings/en-US/Resources.resw`, use `x:Uid` in XAML and `ResourceLoader` in code. |
| L10N-003 | The C++ and Rust engines must share one Win32 `STRINGTABLE` resource file, loaded with `LoadStringW`. |
| L10N-004 | The C# engine must use the same table if Native AOT can embed Win32 resources, and a `.resx` file otherwise. |
| L10N-005 | The engines' strings are the notification area icon tooltip, the menu items, and the message boxes. None of them may name the build (C++, Rust, or C#). |
| L10N-006 | Log lines and pipe protocol text are not user-facing and must stay in English. |
| L10N-007 | The pipe commands and the keys in `settings.json` must never be translated. |

## Writing strings

| ID | Rule |
|---|---|
| L10N-010 | A string must be a whole sentence or label. Code must not build a sentence by joining fragments. |
| L10N-011 | A string with values must use numbered or named placeholders, so word order can change. |
| L10N-012 | A string must leave room to grow: translations can be about 40 percent longer. |
| L10N-013 | Each string must have a stable key that does not change when its English text changes. |

## Checking

| ID | Rule |
|---|---|
| L10N-020 | A pseudo-locale build with stretched, accented text must exist, and a screen with a plain English string in it is a bug. |
| L10N-021 | The right-to-left layout must be checked before a language such as Arabic ships. |

??? note "Status"
    English text is still written in XAML and in the engines' source in places. Moving it into resource files is still to do.
