# When is a file finished?

This is the question the whole tool turns on. Transfer too early and you archive a truncated
acquisition; transfer too late and the instrument PC fills up.

MSmover is deliberately conservative. A false negative costs a few minutes of delay; a false
positive costs data.

## The six checks

A file becomes eligible only when **all** of these hold.

### 1. Not a reparse point

Symlinks, junctions and any other reparse point are dropped outright. Without this the tool would
re-process the symlinks it created itself, in a loop. This replaces the old script's
`fsutil reparsepoint query | find "Symbolic Link"` guard.

### 2. At least `MinSizeBytes`

Default 1 KiB. Skips zero-length placeholders that some software creates at the start of a run.

### 3. Last written at least `MinAgeSeconds` ago

Default 60 seconds.

### 4. The optional companion file exists

`RequireSiblingGlob`, e.g. `{basename}.sld`. This is the general form of the old script's
*"if no `.IDX` exists yet the run has not really started"* rule. Usually unnecessary for a
self-contained single-file format — leave it empty unless your acquisition really does produce a
marker file you can key on.

### 5. Size unchanged across N consecutive probes

`StabilityProbes` readings (default 3), spaced `StabilityIntervalSeconds` apart (default 10). The
counter **restarts** if the size changes, so a file that grows in bursts never accumulates a
passing score.

### 6. It can be opened with `FileShare.None`

This is the primary signal. Thermo Xcalibur holds the `.raw` file open for the whole acquisition,
so an exclusive open succeeding is strong evidence the run has finished and the software has let go.

> [!IMPORTANT]
> Windows does not always update a file's size or modified time in the directory entry until the
> writing handle is closed. That means checks 3 and 5 can *both* look satisfied while a file is
> still being written. The lock test is what actually protects you; the others are there to catch
> the cases where it does not, such as software that closes and reopens its output.

## And then again, immediately before transferring

Time passes between a file becoming eligible and a transfer slot being free. The exclusive open is
therefore repeated at the moment the transfer starts — and the handle is **held for the entire
read**, so nothing can append to the source once MSmover has begun copying it.

If that second attempt fails, the file goes back to *Waiting*. This is reported as a normal state,
not a failure, and does not count against the retry budget: an antivirus scanner or an indexer
grabbing the file briefly is expected, not exceptional.

## Reading the Queue tab

The *Detail* column names the check currently holding a file:

| Detail | Meaning |
|---|---|
| `below minimum size (0 < 1024 bytes)` | check 2 |
| `too recent (12s < 60s)` | check 3 |
| `companion file 'RUN_003.sld' not present yet` | check 4 |
| `size check 1/3 (734003200 bytes)` | check 5, first of three probes |
| `size check 2/3, next probe in 7s` | check 5, waiting out the interval |
| `file is locked by another process` | check 6 — still being acquired |
| `ready` | all six passed, waiting for a transfer slot |

## Discovery: watcher plus rescan

`FileSystemWatcher` gives near-instant notification, but it is not trustworthy on its own:

* Its internal buffer overflows under bursts, and overflowed events are **silently lost**.
* It is unreliable on network sources.
* It sees nothing that happened while MSmover was not running.

So a periodic **full rescan** runs regardless, every `RescanSeconds` (default 300). A buffer
overflow triggers an immediate rescan and a warning in the log. On startup, a full scan runs first,
which is what picks up the backlog.

You can force one at any time with **Scan now**. That also clears the "already handled" list, so
files previously skipped, blocked or given up on are re-evaluated.

## Tuning

The defaults suit 0.1–2 GB Thermo acquisitions on a directly attached disk.

| Situation | Change |
|---|---|
| Very large files, slow disk | Raise `StabilityIntervalSeconds` to 20–30. |
| Software that closes and reopens the file between scans | Raise `MinAgeSeconds` well past the gap, and `StabilityProbes` to 4–5. |
| A network source folder | Raise `RescanSeconds` if the scan is expensive; the watcher is unreliable there anyway. |
| You want it snappier for testing | `MinAgeSeconds` 0, `StabilityProbes` 1, `StabilityIntervalSeconds` 1. **Testing only.** |

> [!CAUTION]
> Lowering `MinAgeSeconds` and `StabilityProbes` together removes two of the three independent
> safeguards and leaves only the lock test. That is fine for a scratch folder you are experimenting
> with, and a bad idea on a live instrument.
