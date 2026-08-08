# Template cookbook

The **target template** decides where a file lands, relative to the rule's target folder. It is a
path with `{token}` placeholders. Both `\` and `/` work as separators, empty segments collapse, and
the result can never escape the target folder.

> [!NOTE]
> Every example on this page is asserted by the test suite
> (`tests/MSmover.Core.Tests/DocumentationExamplesTests.cs`).

## Tokens

| Token | Meaning |
|---|---|
| `{t1}` … `{tN}` | base name split on the delimiter, 1-based |
| `{filename}` | file name with extension |
| `{basename}` | file name without extension |
| `{ext}` | extension without the dot |
| `{relpath}` | sub-folder below the source root (recursive mode only) |
| `{yyyy}` `{yy}` | year |
| `{MM}` `{dd}` | month, day |
| `{HH}` `{mm}` `{ss}` | hour, minute, second |
| `{g:name}` | named capture group from the include regex |
| `{machine}` | this computer's name |
| `{rulename}` | the rule's name |

> [!WARNING]
> Date tokens are **case-sensitive**, following the .NET convention: `{MM}` is the month and
> `{mm}` is the minute. Getting these the wrong way round is the single most common template
> mistake. The other tokens are case-insensitive.

### Date tokens

`{yyyy}` and friends read from the file's **last-modified time** by default, which for a finished
acquisition is when the run ended. Set **Date tokens from** to `Now` on the rule if you would
rather stamp with the transfer time — useful when back-filling an old archive that you want
grouped by when it was ingested.

## The delimiter split

`{t1}`…`{tN}` come from splitting the base name (no extension) on the **delimiter**:

```text
Delimiter  _

MSTEST_A01_003.raw   ->   {t1} = MSTEST   {t2} = A01   {t3} = 003
```

Setting **Delimiter count** to *n* rejects any name that does not have exactly *n* delimiters:

| File | Expected 2 | Verdict |
|---|---|---|
| `MSTEST_A01_003.raw` | 2 delimiters | accepted |
| `MSTEST_A01.raw` | 1 delimiter | `Filename check: too few delimiters (found 1, expected 2). File ignored.` |
| `TOO_MANY_PARTS_HERE_X.raw` | 4 delimiters | `Filename check: too many delimiters (found 4, expected 2). File ignored.` |

This is a real safety net, not decoration: it catches mis-typed sequence entries before they are
filed under a project folder that does not exist. Leave **Require exactly** unticked to disable
the check.

---

## Recipes

Given `MSTEST_A01_003.raw`, acquired 2026-03-14 15:09.

### Flat — no restructuring

```text
{filename}
```
```text
MSTEST_A01_003.raw
```

### The Waters layout (what the old batch script did)

```text
Delimiter            _
Expected delimiters  2
Template             {t1}\{t1}.pro\Data\{filename}
```
```text
MSTEST\MSTEST.pro\Data\MSTEST_A01_003.raw
```

### One folder per project

```text
{t1}\{filename}
```
```text
MSTEST\MSTEST_A01_003.raw
```

### By acquisition date

```text
{yyyy}\{MM}\{dd}\{filename}
```
```text
2026\03\14\MSTEST_A01_003.raw
```

### Project, then date

```text
{t1}\{yyyy}-{MM}\{filename}
```
```text
MSTEST\2026-03\MSTEST_A01_003.raw
```

### Year and week-style bucketing

```text
{yyyy}\{yyyy}-{MM}\{t1}\{filename}
```
```text
2026\2026-03\MSTEST\MSTEST_A01_003.raw
```

### Mirror the source folder structure

Requires **Include sub-folders**. `{relpath}` is empty at the source root and collapses cleanly,
so the same template works at every depth.

```text
{relpath}\{filename}
```
```text
D:\Data\a.raw              ->  a.raw
D:\Data\2026\week12\a.raw  ->  2026\week12\a.raw
```

### Mirror the structure, but under a project folder

```text
{t1}\{relpath}\{filename}
```

### Split by plate, using a capture group

```text
Include regex  (?i)^(?<proj>[^_]+)_(?<plate>[A-H]\d{2})_(?<inj>\d+)\.raw$
Template       {g:proj}\{g:plate}\{filename}
```
```text
MSTEST\A01\MSTEST_A01_003.raw
```

### Separate by instrument, when several PCs write to one share

```text
{machine}\{t1}\{filename}
```
```text
LCMS-03\MSTEST\MSTEST_A01_003.raw
```

### Rename as well as re-file

The last segment of the template is the file name; it does not have to be `{filename}`.

```text
{t1}\{t1}_{t2}_{yyyy}{MM}{dd}.{ext}
```
```text
MSTEST\MSTEST_A01_20260314.raw
```

> [!CAUTION]
> Renaming means two different source files can resolve to the same destination name. The second
> one will be **blocked** with "a file already exists at the target" and left in the source folder
> — nothing is overwritten, but you will have to sort it out by hand. Include something unique
> such as `{t3}` or `{HH}{mm}{ss}` if collisions are possible.

---

## What gets rejected, and why

The template is validated on every file. These verdicts appear in the Queue tab's *Detail* column
and in the log.

| Verdict | Cause | Example |
|---|---|---|
| `NotIncluded` | The include regex did not match. The file is not queued at all. | `notes.txt` against `(?i)\.raw$` |
| `Excluded` | The exclude regex matched. | `BLANK_A01_001.raw` against `(?i)^blank` |
| `TooFewDelimiters` / `TooManyDelimiters` | Wrong number of delimiters. | `MSTEST_A01.raw` with expected 2 |
| `UnknownToken` | A token that does not exist, or `{t9}` when the name splits into 3 parts, or `{g:x}` with no such capture group. | `{nonsense}\{filename}` |
| `EmptyToken` | A token resolved to an empty string, which would create a stray folder. | `_A_B.raw` with `{t1}` |
| `InvalidPath` | A segment contains characters Windows forbids, ends in a dot or space, is `.` or `..`, or the result would escape the target folder. | `MSTEST._A01_003.raw` with `{t1}` gives the segment `MSTEST.` |

The last one deserves a note: a template is built from a *file name*, and a file name is
attacker-controlled in the sense that anyone who can name a file can influence the path. MSmover
resolves the final path and checks it is still inside the target folder before doing anything, so
a name that tries to climb out with `..` is rejected rather than obeyed.

---

## Long paths

A UNC target plus `{t1}\{t1}.pro\Data\{filename}` passes the old 260-character limit more easily
than you would think. MSmover is built long-path aware and prefixes paths for the underlying calls,
so deep templates work — but other tools that later read the archive may not be. Keep the template
shallow if anything downstream is old.

---

## Combining with the index file

Set **Index file** on the rule (e.g. `msmover_index.tsv`) to append one row per completed transfer
at the target root. It is the successor to the old script's `raw_filelist.txt`, with rather more in
it:

```text
timestamp             rule        target                                    size      hash                        source
2026-03-14 15:12:03   Thermo raw  MSTEST\MSTEST.pro\Data\MSTEST_A01_003.raw 1288490188 xxhash64:b2baad2182f1b1ab  D:\Data\MSTEST_A01_003.raw
```

Tab-separated with a header, so `readr::read_tsv()` picks it up directly.
