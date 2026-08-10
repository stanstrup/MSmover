# Troubleshooting

## First: where to look

| Symptom | Look at |
|---|---|
| A specific file is not moving | **Queue** tab, *Detail* column |
| A file is not in the queue at all | The include regex rejected it — see [below](#a-file-never-appears-in-the-queue) |
| A rule is red / not running | **Rules** tab, *Detail* column, and the log |
| Anything else | **Log** tab, or `%APPDATA%\MSmover\logs\` |

The single most useful distinction: **a file missing from the queue entirely was rejected by the
include regex.** A file present but Skipped matched the include regex and failed something later.

---

## A file never appears in the queue

The include regex did not match its name. Common causes:

* **Case.** `\.raw$` does not match `RUN.RAW`. Use `(?i)\.raw$`.
* **Unescaped dot.** `.raw$` matches more than you meant; `\.raw$` is correct.
* **Backslashes in `config.json`.** If you hand-edited the file, `\.raw$` must be written
  `"\\.raw$"`. A single backslash makes the JSON invalid or the pattern wrong.
* **It is in a sub-folder** and **Include sub-folders** is off.
* **It is a symlink** from a previous move — reparse points are deliberately ignored.
* **It is below the minimum size**, or is a `.msmover-part` / `.msmover-link` working file.

Test with the live preview in the rule editor, or **Preview (dry pass)…**.

## A file sits in Waiting forever

Read the *Detail* column; it names the check that is holding it.

| Detail | Meaning | What to do |
|---|---|---|
| `file is locked by another process` | Something holds a handle. Usually acquisition is still running. | Wait. If it never clears, find the holder with Resource Monitor — antivirus and backup agents are the usual suspects. |
| `too recent (12s < 60s)` | Minimum age not reached. | Wait, or lower `MinAgeSeconds`. |
| `size check 1/3 (…)` | The file is still growing, or the probes have not accumulated yet. | Wait `StabilityProbes × StabilityIntervalSeconds`. |
| `companion file '…' not present yet` | `RequireSiblingGlob` is set and the file is missing. | Clear the setting if the instrument does not produce that file. |
| `below minimum size` | Smaller than `MinSizeBytes`. | Lower it, or accept that this file is a stub. |

> [!TIP]
> A file that has genuinely finished but stays locked is almost always an antivirus real-time
> scanner. Excluding the acquisition folder from on-access scanning is the usual fix, and generally
> a good idea on an instrument PC anyway.

## "Target already exists, source left untouched"

A file is already at the resolved destination. MSmover never overwrites, so it stops and leaves
both files alone.

* If the target is the same file from an earlier run, delete whichever copy you do not want.
* If two different source files resolve to the same name, your template is losing information —
  see [renaming collisions](../cookbooks/templates.md#rename-as-well-as-re-file).

Then press **Scan now** to re-evaluate.

## "Filename check: too few / too many delimiters"

The name does not have exactly the configured number of delimiters. Either the name is wrong, or
your **Require exactly** setting is. Untick it to disable the check entirely.

## A rule shows as Faulted

The *Detail* column on the Rules tab gives the reason. Common ones:

| Fault | Fix |
|---|---|
| `Source folder does not exist` | The path is wrong, or a mapped drive is not connected yet in this session. Prefer UNC paths. |
| symlink pre-flight failed | See [Symlinks](symlinks.md). |
| `Verification cannot be disabled in Move mode` | Set verification back to `Hash` or `Size`. |
| `Target is inside the source folder while recursive is on` | Transfers would feed themselves. Move the target elsewhere. |
| `Include regex is invalid: …` | Fix the pattern. |

## Nothing happens at all

* Is the rule **Enabled**? New rules start disabled.
* Is **Pause** on? It survives restarts, so it may have been left on from last session.
* Is **Global dry run** on? The toolbar button says `Global dry run: ON` and the log warns at
  startup.
* Is the rule in its own **Dry run**? The Rules tab shows `Copy (dry run)` in the *Mode* column.

## Transfers are slow

* Check the reported rate in the log: `(1.2 GB, 96.4 MB/s, XxHash64:…)`.
* Verification reads the file back, so a hash-verified transfer moves roughly 2× the bytes over the
  network. That is the cost of knowing it arrived intact.
* Raise **Copy buffer** in Settings (default 1024 KB) for high-latency shares.
* **Max concurrent transfers** across all rules defaults to 2. Raising it helps only if the link is
  not already saturated.

## Long path errors

MSmover is long-path aware, but downstream tools may not be. If something else fails to open the
archived files, shorten the template — `{t1}\{t1}.pro\Data\{filename}` under a deep UNC path adds
up quickly.

## A file was transferred before and is not picked up again

Expected, as long as the earlier copy is **still at the target**. A file is skipped only while a
transfer of it is still sitting where it was put; the log says so once per session:

```text
INFO  Thermo raw | 12 file(s) were already transferred by this rule and are still present at the
                   target, so they were skipped.
```

Three ways to make it transfer again:

* **Delete it at the target.** It becomes eligible on the next scan, with no other action. This is
  the usual case when re-running a test.
* **Select it on the Queue tab and press Retry selected.** Note this does not overwrite: if the
  target file is still there, the file simply reports as blocked.
* **Scan now**, which re-evaluates every file for the rule.

Renaming the rule has the same effect, because the journal is keyed on the rule name — but then
every file still at the target is reported as a clash, so it is a blunt instrument.

## Files reappear after "Scan now"

Expected. **Scan now** re-evaluates everything, including files previously skipped, blocked or
given up on. Anything still present at the target settles back to being skipped quietly.

## The log is too quiet or too noisy

**Settings → Logging → Level.** `Debug` adds per-file queueing decisions, which is what you want
when working out why a file is not being picked up. `Warn` reduces it to problems only.

Log files live in `%APPDATA%\MSmover\logs\`, one per day, kept 14 days by default.

## Starting a second copy does nothing

By design — MSmover is single-instance. Launching it again brings the existing window to the front.
Look for the tray icon.

## Reporting a problem

Include:

* the relevant chunk of `%APPDATA%\MSmover\logs\msmover-YYYYMMDD.log`,
* the rule's settings (`config.json`, with paths redacted if you like),
* what the *Detail* column said.
