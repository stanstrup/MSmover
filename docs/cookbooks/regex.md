# Regex cookbook

Two rule settings take a regular expression:

* **Include regex** — a file is considered only if this matches. Empty means "match everything".
* **Exclude regex** — a file is dropped if this matches. Empty means "exclude nothing".

Both use the .NET regex flavour (`System.Text.RegularExpressions`): PCRE-like, with `\d`, `\w`,
named groups and lookaround. POSIX classes such as `[[:digit:]]` are **not** supported — use `\d`.

> [!NOTE]
> Every example on this page is asserted by the test suite
> (`tests/MSmover.Core.Tests/DocumentationExamplesTests.cs`), so if the behaviour ever changes the
> build fails rather than the documentation quietly going stale.

## The five rules that catch everyone

### 1. It is matched against the file *name*, never the path

`MSmover` passes only `MSTEST_A01_003.raw` to the regex — not `D:\Data\2026\MSTEST_A01_003.raw`.
A pattern like `2026.*\.raw$` will therefore never match a file just because it sits in a `2026`
folder. To route by folder, use the `{relpath}` [template token](templates.md) instead.

### 2. It is *not* anchored

`Regex.Match` looks for the pattern anywhere in the name. So:

| Pattern | `QC_A01_001.raw` | `MY_QC_A01_001.raw` |
|---|---|---|
| `QC_` | match | **also a match** |
| `^QC_` | match | no match |

Anchor with `^` when you mean "starts with", `$` when you mean "ends with".

### 3. `.` means "any character"

`.raw$` matches `Xraw` and `9raw`. Escape it:

| Pattern | Means |
|---|---|
| `.raw$` | any character, then `raw`, at the end |
| `\.raw$` | a literal dot, then `raw`, at the end — **what you want** |

### 4. It is case-sensitive unless you say otherwise

Instruments are inconsistent about `.raw` versus `.RAW`. Prefix the pattern with the inline
option `(?i)`:

```regex
(?i)\.raw$
```

That is why the default include regex is `(?i)\.raw$` and not `\.raw$`.

### 5. In `config.json`, backslashes are doubled

The GUI takes the pattern literally. The JSON file does not — JSON uses `\` as its own escape
character, so a pattern typed as `(?i)\.raw$` is stored as:

```json
"IncludeRegex": "(?i)\\.raw$"
```

If you hand-edit `config.json`, double every backslash. `\d` becomes `\\d`, `\.` becomes `\\.`.

---

## Selecting files

| Goal | Include regex |
|---|---|
| Any `.raw` file (the default) | `(?i)\.raw$` |
| Only names starting with `QC_` | `(?i)^QC_.*\.raw$` |
| One of several project prefixes | `(?i)^(PLASMA\|SERUM\|URINE)_.*\.raw$` |
| Exactly `PROJECT_WELL_INJECTION` | `(?i)^[^_]+_[A-H]\d{2}_\d+\.raw$` |
| Name begins with an 8-digit date | `^\d{8}_.*\.raw$` |
| `.raw` or `.mzML` | `(?i)\.(raw\|mzML)$` |
| Six or more characters before the first underscore | `(?i)^[^_]{6,}_.*\.raw$` |

### Reading one of those, piece by piece

The fourth row above is the most involved. It says "a project name, an underscore, a well position,
an underscore, an injection number, then `.raw` — and nothing else".

```regex
(?i)^[^_]+_[A-H]\d{2}_\d+\.raw$
```

| Piece | Reads as | Matches | Does not match |
|---|---|---|---|
| `(?i)` | ignore case from here on | `.RAW`, `.raw` | — |
| `^` | start of the name | — | anything with text before the project |
| `[^_]+` | one or more characters that are **not** an underscore | `PLASMA` | `PLASMA_X` (the split would be wrong) |
| `_` | a literal underscore | | |
| `[A-H]` | exactly one letter from A to H | `C` | `Z`, `c` — well, `c` matches here only because of `(?i)` |
| `\d{2}` | exactly two digits | `03` | `3`, `003` |
| `_` | a literal underscore | | |
| `\d+` | one or more digits | `011`, `7` | `A11` |
| `\.raw` | a literal dot, then `raw` | `.raw` | `Xraw` |
| `$` | end of the name | — | `PLASMA_C03_011.raw.bak` |

Read that way, `PLASMA_C03_011.raw` matches and `PLASMA_Z99_011.raw` does not, because `Z` is
outside `[A-H]`. That is the point of writing it out rather than using `.*`: the pattern rejects a
well position that cannot exist on a 96-well plate, so a typo in the sequence gets caught rather
than filed.

## Excluding files

Exclude is evaluated after include and **wins**. It is usually clearer than building negation into
the include pattern.

| Goal | Exclude regex |
|---|---|
| Skip blanks, washes and standards | `(?i)^(blank\|wash\|std)[_-]` |
| Skip anything with `test` anywhere in the name | `(?i)test` |
| Skip conditioning injections | `(?i)^cond\d*_` |
| Skip names ending in `_bad` before the extension | `(?i)_bad\.raw$` |
| Skip temporary or partial names | `^[~$]` |

### Negative lookahead, if you must do it in one pattern

```regex
(?i)^(?!blank|wash|std)[^_]+_[A-H]\d{2}_\d+\.raw$
```

| Piece | Reads as |
|---|---|
| `^` | at the start of the name… |
| `(?!blank\|wash\|std)` | …assert that what follows is **not** `blank`, `wash` or `std`. Consumes nothing — the cursor stays at position 0. |
| `[^_]+_[A-H]\d{2}_\d+\.raw$` | …then match the ordinary pattern from there |

The "consumes nothing" part is what makes lookahead confusing. `(?!…)` is a *check* at the current
position, not a piece of the name. So after it, `[^_]+` still starts matching at the very beginning
— which is why `BLANK_A01_003.raw` fails (the check rejects it) but `PLASMA_A01_003.raw` passes and
then matches from `P`.

It works, but two readable patterns beat one clever one, and when include and exclude are separate
the Queue tab can tell you which of them rejected a file.

---

## Capture groups drive the destination

A **named** capture group in the include regex becomes a `{g:name}` token in the target template.
This is the most powerful routing mechanism MSmover has.

### Project / plate / injection

This is the pattern from [Reading one of those, piece by piece](#reading-one-of-those-piece-by-piece)
with names wrapped around the three interesting parts. Wrapping a piece in `(?<name>…)` changes
nothing about what the pattern matches — it only gives that piece a label you can use later.

```regex
(?i)^(?<proj>[^_]+)_(?<plate>[A-H]\d{2})_(?<inj>\d+)\.raw$
```

| Group | Pattern inside it | For `PLASMA_C03_011.raw` | Template token |
|---|---|---|---|
| `proj` | `[^_]+` — everything up to the first underscore | `PLASMA` | `{g:proj}` |
| `plate` | `[A-H]\d{2}` — a well position | `C03` | `{g:plate}` |
| `inj` | `\d+` — the injection number | `011` | `{g:inj}` |

Note `inj` is captured but not used below. Capturing something you do not route on is fine and
often worth doing: it documents the name format, and it is there the day you want it.

```text
Template   {g:proj}\{g:plate}\{filename}

PLASMA_C03_011.raw  ->  PLASMA\C03\PLASMA_C03_011.raw
```

### A date embedded in the file name

Split the date into separate groups so the template can build a folder tree from it. One `{g:date}`
group giving `20260314` cannot be sliced afterwards.

```regex
^(?<y>\d{4})(?<m>\d{2})(?<d>\d{2})_(?<proj>[^_]+)_.*\.raw$
```

The three date groups sit directly against each other with no separator between them. That works
because each one is a **fixed width**: `\d{4}` takes exactly four digits, so `\d{2}` after it can
only start at the fifth. Without the `{4}` and `{2}` counts, `\d+` would swallow the whole date.

| Group | Pattern | For `20260314_PLASMA_003.raw` |
|---|---|---|
| `y` | `\d{4}` | `2026` |
| `m` | `\d{2}` | `03` |
| `d` | `\d{2}` | `14` |
| `proj` | `[^_]+` after the underscore | `PLASMA` |
| — | `_.*\.raw$` — whatever is left, then the extension | `_003.raw` |

```text
Template   {g:y}\{g:m}\{g:d}\{g:proj}\{filename}

20260314_PLASMA_003.raw  ->  2026\03\14\PLASMA\20260314_PLASMA_003.raw
```

> [!TIP]
> If the date you want is the *acquisition* time rather than something in the name, use the
> `{yyyy}` `{MM}` `{dd}` [date tokens](templates.md#date-tokens) instead — no regex needed.

### Optional segments

Make a group optional with `?`, and give it something to fall back on. A group that matched nothing
is an error, not an empty string, so prefer alternation over an optional group:

```regex
(?i)^(?<proj>[^_]+)_(?<mode>POS|NEG)_(?<inj>\d+)\.raw$
```

```text
Template   {g:proj}\{g:mode}\{filename}

LIPID_POS_004.raw  ->  LIPID\POS\LIPID_POS_004.raw
LIPID_NEG_004.raw  ->  LIPID\NEG\LIPID_NEG_004.raw
```

### Numbered groups work too, but name them anyway

An unnamed group such as `([^_]+)` is addressable by position: `{g:1}`, `{g:2}`, and so on. That
works because .NET's group collection accepts a numeric name.

```text
Include regex  (?i)^([^_]+)_.*\.raw$
Template       {g:1}\{filename}

MSTEST_A01_003.raw  ->  MSTEST\MSTEST_A01_003.raw
```

Prefer names regardless. `{g:proj}` says what it means, and it does not silently start pointing at
something else the day someone inserts another group earlier in the pattern.

A `{g:…}` that names a group which does not exist, or one that did not participate in the match, is
reported as `UnknownToken` rather than quietly resolving to an empty string.

---

## Choosing between capture groups and the delimiter split

For names that are simply "fields joined by a separator", the delimiter split is easier and gives
you a free structural check:

```text
Delimiter            _
Expected delimiters  2
Template             {t1}\{t1}.pro\Data\{filename}
```

Any name without exactly two underscores is rejected with a clear message, which is a genuine
safety net against a mis-typed sequence. See the [template cookbook](templates.md#the-delimiter-split).

Reach for a regex when you need to *validate the shape* of each field, not just count them —
`[A-H]\d{2}` for a well position, `\d{8}` for a date — or when the separator is inconsistent.

You can use both: the delimiter split and the capture groups are computed from the same file name
and both are available to the template.

---

## Testing your patterns

1. **The live preview in the rule editor.** Type a file name, watch it resolve or fail. Instant.
2. **Preview (dry pass)** on the Rules tab. Runs the pattern over everything currently in the
   source folder and shows a verdict per file. This is the one to use before arming a rule.
3. **The Queue tab.** A file that fails the include regex is not queued at all. A file that passes
   include but fails the naming rule appears as **Skipped** with the reason in *Detail*.

That difference matters when debugging: **if a file is missing from the queue entirely, the include
regex rejected it.** If it is present but Skipped, the include regex matched and something later
went wrong.

---

## Quick reference

| | |
|---|---|
| `^` `$` | start / end of the name |
| `.` | any single character |
| `\.` | a literal dot |
| `\d` `\w` `\s` | digit / word character / whitespace |
| `[A-H]` | one character in the range |
| `[^_]` | any character except `_` |
| `*` `+` `?` | zero or more, one or more, zero or one |
| `{2}` `{2,}` `{2,4}` | exactly / at least / between |
| `(a\|b)` | either |
| `(?<name>…)` | named capture, usable as `{g:name}` |
| `(?:…)` | group without capturing |
| `(?!…)` | not followed by |
| `(?i)` | case-insensitive from here on |
