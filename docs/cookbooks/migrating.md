# Migrating from `file_mover`

The predecessor was `QC4Metabolomics/file_mover`: `watchexec.exe` watching for `*/_extern.inf`,
firing `win_waters_mover.bat`, which did the work with `robocopy /MOVE` and `mklink /D`.

The biggest change is not in the tool. It is that the new instrument writes `.raw` **files**, where
Waters wrote `.raw` **folders**. Everything that keyed on folder contents — `_extern.inf` to notice
a run, `*.IDX` to know it had really started, a loop over every file inside to check for locks —
has no direct equivalent, and is replaced by checks on a single file.

## Settings, translated

| Old | New |
|---|---|
| `set "infolder=…"` | **Source folder** |
| `set "outfolder=…"` | **Target folder** |
| `set "delim=_"` | **Delimiter** |
| `set "expect_delims=2"` | **Require exactly** `2` delimiters |
| `set "symlinkback=TRUE"` | **Leave a symbolic link at the original location** |
| hardcoded `%%a\%%a.pro\Data\%%~nxi` | **Target template** `{t1}\{t1}.pro\Data\{filename}` |
| `for /d /r` | **Include sub-folders** |
| `robocopy … /E /MOVE` | **Mode** = `Move` |
| `raw_filelist.txt` | **Index file** `msmover_index.tsv` |
| `watchexec -w … -f "*/_extern.inf"` | built in: `FileSystemWatcher` plus a periodic rescan |
| `enable_monitor_at_startup.bat` | **Settings → Start MSmover automatically when I log in** |
| `clear_symlink_folders.bat` | not needed — MSmover skips reparse points by design |

A rule reproducing the old behaviour exactly:

```text
Delimiter            _
Require exactly      2 delimiters
Target template      {t1}\{t1}.pro\Data\{filename}
Include regex        (?i)\.raw$
Mode                 Move
Leave a symbolic link at the original location   yes
If the target exists Skip, leave the source untouched, and warn
```

## Behaviour, translated

| Old script | MSmover |
|---|---|
| `fsutil reparsepoint query \| find "Symbolic Link"` then skip | Reparse points are dropped by the completion gate. Same intent, no process spawn. |
| `if "%%b" == "" (echo … Too few delimiters …)` | `Filename check: too few delimiters (found 1, expected 2). File ignored.` — same message, now also shown per file in the Queue tab. |
| `if not "%%c" == "" (echo … Too many delimiters …)` | Likewise for too many. |
| `if exist "…\%%~nxi" echo raw folder already exists! Folder ignored.` | `Target already exists, source left untouched: …` Same policy: never overwrite, never delete. |
| `if exist "%%~fi\*.IDX"` — the run has really started | **Required companion file**, e.g. `{basename}.sld`. Optional, and usually unnecessary for a single-file format. |
| `powershell [System.IO.File]::Open(…,'Write')` per file, retry every 10 s | Native exclusive open (`FileShare.None`), no process spawn, and the handle is then **held for the whole copy** so nothing can append mid-transfer. |
| `timeout /t 10` then transfer | Minimum age, N consecutive stable size probes, then the lock test — all configurable. |
| `mklink /D` after the move | Symlink created **before** the source is deleted, so a failure leaves the source in place. Pre-flighted before the rule even starts. |
| `echo … >> raw_filelist.txt` | Index file with timestamp, rule, target, size and hash. |

## What you gain

* **Verification.** `robocopy` copies; it does not read the destination back and compare a hash. The
  old script deleted the source on robocopy's word. MSmover re-reads the file from the network and
  compares before deleting anything.
* **A dry run.** There was no way to ask the batch script what it would do.
* **Symlink pre-flight.** `mklink /D` failing left you with data moved and no link. MSmover refuses
  to start the rule instead — and note that the old script used `/D` (a *directory* link), which is
  not what a single-file target needs.
* **Crash recovery.** An interrupted `robocopy` could leave a plausible-looking partial file at the
  destination. MSmover writes to `.msmover-part` and cleans up orphans on the next launch.
* **Ordering.** Newest first, so the run you are waiting for moves before the backlog.
* **Multiple rules**, a log you can read, and a queue you can watch.

## What to watch out for

* **`mklink /D` versus a file symlink.** The old command made directory symlinks because the target
  was a folder. MSmover creates file symlinks. Both need `SeCreateSymbolicLinkPrivilege`, and
  pointing at a network target additionally needs local-to-remote evaluation turned on — see
  [Symlinks](../articles/symlinks.md). The old script never checked, so this may be the first time
  you find out the setting was never enabled.
* **Copy mode leaves the source in place**, so the same file would be rediscovered forever. MSmover
  remembers what it has transferred in `journal.jsonl`; **Scan now** deliberately forgets, and will
  re-report already-copied files as blocked once.
* **The `.pro\Data` layout was a MassLynx convention.** If the new instrument is Thermo, you
  probably want a simpler template. `{t1}\{filename}` or `{yyyy}\{MM}\{filename}` are more likely
  to be what you actually want — see the [template cookbook](templates.md).

## Suggested cut-over

1. Point a rule at the new instrument's folder in **dry run**, and read the log for a day.
2. Switch to **Copy** for real. Now both the source machine and the archive have the data.
3. Once you trust it, switch to **Move**, with the symlink option if you want the old paths to
   keep working.
