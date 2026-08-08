# Getting started

## Install

Copy `MSmover.exe` anywhere on the instrument PC and run it. There is nothing to install: it is a
self-contained .NET 8 build, so it needs no runtime, and it runs as a normal user.

First launch creates `%APPDATA%\MSmover\` and shows an empty **Rules** tab.

> [!TIP]
> Turn on **Settings → Start MSmover automatically when I log in**. It deliberately runs inside
> your logged-in session rather than as a service: a mapped drive letter only exists in a user
> session, and symlink creation uses that user's privileges.

## Your first rule

**Rules → Add rule…**, then fill in the minimum:

| Field | Value |
|---|---|
| Name | `Thermo raw` |
| Source folder | wherever the instrument writes, e.g. `D:\Xcalibur\Data` |
| Target folder | the network location, e.g. `\\storage\ms\incoming` |
| Include regex | `(?i)\.raw$` (the default) |
| Mode | `Copy` to begin with |
| Target template | `{filename}`, or see the [template cookbook](../cookbooks/templates.md) |

New rules are created **disabled and in dry run**. That is deliberate.

### The live preview

While you type, the **Resolves to** box under the template shows where a test file name would
land, or in red exactly why it would be skipped:

```text
Test with file name   MSTEST_A01_003.raw
Resolves to           \\storage\ms\incoming\MSTEST\MSTEST.pro\Data\MSTEST_A01_003.raw
```

Change the test name to something malformed and it tells you the verdict instead:

```text
Test with file name   MSTEST_A01.raw
Resolves to           TooFewDelimiters: Filename check: too few delimiters (found 1, expected 2).
                      File ignored.
```

This is the fastest way to get a template and a regex right. Use it before arming anything.

## Prove it before you arm it

MSmover is built so that you never have to find out the hard way.

### 1. Dry run and preview

With the rule enabled but **Dry run** on, select it and press **Preview (dry pass)…**. That walks
the current contents of the source folder and shows a table:

| File | Size | Modified | Verdict | Target, or why not |
|---|---|---|---|---|
| `PLASMA_C03_012.raw` | 2.3 MB | 2026-03-14 15:09 | would copy | `\\storage\ms\incoming\PLASMA\PLASMA.pro\Data\PLASMA_C03_012.raw` |
| `BADNAME_ONLYONE.raw` | 2.5 MB | 2026-03-14 15:02 | skip | Filename check: too few delimiters (found 1, expected 2). File ignored. |
| `QC_A01_009.raw` | 1.1 GB | 2026-03-14 15:44 | wait | still open in another process — probably still being acquired |

Nothing is opened for writing and nothing is created. **Copy to clipboard** gives you the same
table as TSV if you want to check it in R.

The running rule also logs `DRY RUN  WOULD COPY …` lines for everything it would have done, so you
can leave it in dry run over a few real acquisitions and read the log afterwards.

### 2. Copy for real

Turn **Dry run** off, leave **Mode** on `Copy`. Nothing is ever deleted in copy mode. Confirm files
arrive where you expect and that the hashes appear in the log:

```text
COPY OK  MSTEST_A01_001.raw  ->  \\storage\ms\incoming\MSTEST\MSTEST.pro\Data\MSTEST_A01_001.raw
         (1.2 GB, 96.4 MB/s, XxHash64:b2baad2182f1b1ab)
```

### 3. Switch to Move

Only now change **Mode** to `Move`. MSmover asks for confirmation, because this is the point at
which source files start being deleted — after verification, but deleted nonetheless.

## Day-to-day

| Want to… | Do this |
|---|---|
| Stop transfers for a moment | **Pause** on the toolbar or the tray menu. Discovery keeps running; the in-flight file finishes. |
| Force a re-check | **Scan now**. This also clears the "already handled" list, so previously skipped, blocked or failed files get a fresh evaluation. |
| See why a file is sitting there | **Queue** tab, *Detail* column — it names the check that is holding it. |
| Stop everything writing, immediately | **Global dry run** on the toolbar. It overrides every rule. |
| Read the history | **Log** tab, or `%APPDATA%\MSmover\logs\`. |

## Safe to interrupt

Closing the window hides it to the tray; only **Quit** from the tray menu exits. If you quit during
a transfer, MSmover warns you and then discards the partial destination file — the source is
untouched, and the next launch cleans up any leftover `.msmover-part` file.

## Next

* [Rules explained](rules.md) — every setting
* [Regex cookbook](../cookbooks/regex.md) — choosing which files to act on
* [Template cookbook](../cookbooks/templates.md) — deciding where they go
* [Migrating from `file_mover`](../cookbooks/migrating.md) — the old batch script, translated
