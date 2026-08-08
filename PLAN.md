# MSmover — design & build plan

A Windows tray application that watches an LC-MS instrument's acquisition folder and
transfers finished `.raw` files to a network drive, safely, with verification.

Successor to `QC4Metabolomics/file_mover` (`win_waters_mover.bat` + `watchexec`),
adapted from Waters `.raw` **folders** to Thermo `.raw` **files**.

---

## 1. Why build rather than buy

Three searches (general folder-watchers, lab-specific tooling, sync suites) found no
tool with the required combination. Closest candidates:

| Tool | Free? | Write-complete detect | Filename→subpath | Verify hash before delete | Dry-run | Newest-first |
|---|---|---|---|---|---|---|
| Limagito File Mover | Lite = 1 rule; €285 single-machine | Yes | Yes | Not documented | No | No |
| DropIt (OSS) | Yes | No | Yes | No | No | No |
| FreeFileSync + RealTimeSync | Yes | Yes (idle time) | No | No | Partial | No |
| File Juggler | $50 | Partial | Unclear | No | No | No |
| GoodSync / Syncovery / Bvckup2 | Paid | Partial | No | Some | No | No |
| PNNL DMS Capture Task Manager (OSS) | Yes | Yes | Yes | Yes | No | No |

Lab-specific tools (Rapid-QC-MS, AutoQ4MS, AutoQC/Panorama) are QC dashboards with
Python/MATLAB/Skyline dependencies, not movers. PNNL's DMS Capture Task Manager does
the copy→validate→archive flow correctly but is a plugin inside an institutional
pipeline, not a standalone app.

The gap that forces a build: **dry-run + copy→hash-verify→then-delete + symlink-back +
newest-first ordering**, in one dependency-free executable.

## 2. Decisions taken

| Question | Decision |
|---|---|
| Target sub-path | Configurable template with tokens; old Waters layout reproducible as a preset |
| Instrument | Thermo, one self-contained `.raw` file per run |
| Link back | Real symlink only, with a pre-flight capability check |
| Runtime | .NET 8 self-contained single-file `.exe`; admin available on the instrument PC |
| Rules | Multiple named rules |
| Target file exists | Skip, leave source untouched, warn (same as old script) |
| Verification | Paranoid always on: copy → read back → hash compare → then delete |
| Startup | Autostart at user login, tray app |
| Backlog at start | Full scan, newest mtime first |
| File sizes | 0.1–2 GB per file |
| Repo / licence | New git repo in `MSmover`, MIT |
| rsync | **Not used** — see §3 |

## 3. Why not rsync

- Every working rsync on Windows (cwRsync, MSYS2, Git-for-Windows) requires the
  Cygwin/MSYS runtime — that breaks the "no dependencies" requirement.
- rsync does **not** accept UNC paths (`\\server\share\...`); it would require
  `net use Z:` first, which is fragile at login before the network is up.
- The delta-transfer algorithm buys nothing here. These are brand-new files with no
  prior version at the destination, so rsync degrades to a whole-file copy — with a
  worse error surface than a native copy.
- Pure-Rust/Go reimplementations (`oc-rsync`, `resync`, `rsync-go`) exist and are
  static, but are young and unproven for this use.

**Instead:** a native chunked copy that computes the source hash in the same pass,
then an independent read-back and hash of the destination. That is a *stronger*
guarantee than `rsync` without `--checksum`.

**Escape hatch:** an optional per-rule "external command" hook (`{src}`, `{dst}`
placeholders) so `robocopy` or an rsync binary can be substituted later without
changing the app. Verification still runs afterwards.

## 4. Stack

| Layer | Choice | Rationale |
|---|---|---|
| Language | C# / .NET 8 (LTS) | Native Win32 file semantics, `FileSystemWatcher`, `File.CreateSymbolicLink` |
| UI | WinForms | Tray icon, log window and settings forms are all first-class; mature, small, no web layer |
| Packaging | `SelfContained=true, PublishSingleFile=true` | One `.exe` (~80 MB), no runtime install, no admin to run |
| Hashing | `System.IO.Hashing` (xxHash64) + `System.Security.Cryptography` | xxHash64 ≈ several GB/s so hashing is never the bottleneck; SHA-256/MD5 selectable |
| Logging | Serilog + `Serilog.Sinks.File` + custom in-memory sink | Rolling files for free, plus a live feed to the log window |
| Tests | xUnit | |

Not NativeAOT — WinForms does not support it. Not Fyne (binary bloat, weak tray),
not Tauri/Wails (WebView2 dependency and a web layer this app does not need).

Build prerequisite on the dev machine: `choco install dotnet-sdk`
(runtimes 6/8/9/10 are already present; the SDK is not).

## 5. Solution layout

```
MSmover/
├─ MSmover.sln
├─ src/
│  ├─ MSmover.Core/            # net8.0, no UI — fully unit-testable
│  │   ├─ Config/              # RuleConfig, AppConfig, JSON load/save
│  │   ├─ Naming/              # PathMapper — template + regex + validation
│  │   ├─ Detection/           # StabilityGate, FileWatcher, RescanTimer
│  │   ├─ Transfer/            # TransferEngine, Hasher, Verifier, SymlinkService
│  │   ├─ Queue/               # PendingSet, priority scheduler, pause
│  │   └─ Journal/             # append-only JSONL state + crash recovery
│  └─ MSmover.App/            # net8.0-windows, WinForms
│      ├─ TrayIcon, MainForm, RuleEditorForm, LogView, QueueView
│      └─ app.manifest (longPathAware), icon, single-instance mutex
└─ tests/
   └─ MSmover.Core.Tests/      # xUnit incl. fault-injection harness
```

## 6. Core design

### 6.1 Detection — is the file finished?

A file becomes eligible only when **all** of the following hold. This is deliberately
belt-and-braces; a false negative costs a few minutes of delay, a false positive costs
data.

1. Not a reparse point — `File.GetAttributes(p) & FileAttributes.ReparsePoint` is 0.
   (Replaces the old `fsutil reparsepoint query | find "Symbolic Link"` check; stops
   the tool re-processing its own symlinks.)
2. Matches the include regex, and does not match the exclude regex.
3. `LastWriteTimeUtc` is older than `MinAgeSeconds` (default 60).
4. Size is at least `MinSizeBytes` (default 1 KiB) — skips stubs.
5. Size is unchanged across `StabilityProbes` consecutive probes (default 3) spaced
   `StabilityIntervalSeconds` apart (default 10).
6. The file can be opened `FileShare.None` — an exclusive open proves no other process
   holds a handle. Thermo Xcalibur holds the `.raw` open for the duration of
   acquisition, so this is the primary signal. (The old script used a PowerShell
   `File.Open(...,'Write')` per file in the folder; same idea, done natively and
   without spawning a process.)
7. *Optional per rule:* a required sibling file exists, e.g. glob `{basename}.sld`.
   This is the generic form of the old script's "if no `*.IDX` exists the run has not
   really started" guard.

The exclusive-open test is **repeated immediately before the transfer begins**, since
time passes between eligibility and dispatch.

### 6.2 Watching — event-driven with a mandatory fallback

- `FileSystemWatcher` per rule (`Created`, `Renamed`, `Changed`), `IncludeSubdirectories`
  from the recursive option, `InternalBufferSize` raised to 64 KB.
- `Error` event (`InternalBufferOverflowException`) triggers an immediate full rescan —
  overflow is silent data loss otherwise.
- A periodic full rescan (default every 5 min, configurable) runs regardless. This
  covers watcher gaps, files that appeared while paused, network sources where
  `FileSystemWatcher` is unreliable, and app restarts.
- Both paths feed one `PendingSet` keyed by full path, so duplicates collapse.

### 6.3 Path mapping

Reproduces and generalises the old script's logic.

```
Delimiter:            _
Expected delimiters:  2          (optional; null = no check)
Template:             {t1}\{t1}.pro\Data\{filename}
```

Tokens:

| Token | Meaning |
|---|---|
| `{t1}`…`{tN}` | basename split on the delimiter |
| `{filename}` `{basename}` `{ext}` | full name / without extension / extension |
| `{relpath}` | source subfolder, recursive mode only |
| `{yyyy}` `{MM}` `{dd}` `{HH}` `{mm}` | from file mtime (or now — selectable) |
| `{g:name}` | named capture group from the include regex |
| `{machine}` `{rulename}` | host name, rule name |

Validation, mirroring the old messages:
- delimiter count < expected → *"Filename check: too few delimiters. File ignored."*
- delimiter count > expected → *"Filename check: too many delimiters. File ignored."*
- referenced token missing → skip with a clear reason
- result sanitised: invalid path chars rejected, `..` traversal blocked, absolute
  paths from tokens blocked

Long paths: `\\?\` prefixing plus `longPathAware` in the manifest. Necessary — a UNC
target plus `{t1}\{t1}.pro\Data\` easily exceeds 260 characters.

The rule editor has a **live preview**: type a filename, see the resolved target path
or the exact rejection reason. This is the main defence against a mis-typed template.

### 6.4 Transfer — the safety-critical sequence

```
 1. Re-acquire exclusive lock on source.        fail → requeue, do nothing
 2. Resolve target path, create target dirs.
 3. If final target exists → log WARNING, leave source, mark blocked, STOP.
 4. Stream source → target as "<name>.msmover-part",
    1 MiB chunks, computing the source hash in the same pass.
 5. Flush(true) — force to the storage device.
 6. Re-open the .part FROM THE NETWORK and hash it independently.
 7. Compare length AND hash.
      mismatch → delete .part, log ERROR, retry (backoff, max N).
                 SOURCE IS NEVER TOUCHED.
 8. Rename .part → final name (atomic within the directory).
 9. Copy source mtime/ctime onto the target.
10. MOVE mode only, in this order:
      a. create symlink at "<source>.msmover-link" → target   (if enabled)
      b. delete source file
      c. rename the symlink to the source's original name
    COPY mode: nothing is deleted, ever.
11. Append to journal; append to the optional target index file.
```

Key properties:
- The `.part` suffix means an interrupted transfer can never leave a plausible-looking
  complete file at the target.
- Step 10 orders symlink creation *before* deletion, so a symlink failure leaves the
  source intact (belt to the pre-flight check's braces).
- There is no code path that deletes a source file that has not been byte-verified at
  the destination.

Retries use exponential backoff. Loss of the network target (`DirectoryNotFoundException`,
`IOException`) suspends the rule and retries rather than failing items permanently.

### 6.5 Deletion propagation

**Architectural guarantee, not a checkbox.** MSmover is strictly one-way and per-file:
it never enumerates the target to reconcile it against the source, and contains no code
path that deletes anything in the target directory. Mirror/sync semantics are
deliberately not implemented. The Settings tab states this explicitly.

*(Flagging this as a deviation from "should be default" — a default implies a toggle.
Say the word if you want it as a setting instead; my recommendation is that removing
the capability entirely is safer than defaulting it off.)*

### 6.6 Symlink handling

`File.CreateSymbolicLink` (.NET 6+). Two obstacles, both checked up front:

1. **`SeCreateSymbolicLinkPrivilege`** — needs elevation or Developer Mode
   (`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock\
   AllowDevelopmentWithoutDevLicense`).
2. **SMB symlink evaluation** — local-to-remote symlink following is **disabled by
   default** on Windows and must be enabled with an elevated
   `fsutil behavior set SymlinkEvaluation L2R:1`. Since the target is a network drive,
   this is the case that will actually bite. Current state is readable via
   `fsutil behavior query SymlinkEvaluation`.

**Pre-flight:** when a rule with symlinks enabled starts, create and delete a test
symlink pointing at the real target. On failure the rule refuses to run, the tray icon
goes red, and the log offers a one-click elevated fix that runs the required `fsutil`
command and explains Developer Mode.

Caveat worth knowing: instrument software that later *writes* to a symlinked path will
be writing over SMB. For read-back of archived runs this is exactly what you want.

### 6.7 Queue and ordering

- Priority queue ordered by `LastWriteTimeUtc` **descending** by default (newest first,
  as requested); ascending selectable per rule.
- One worker per rule by default (configurable 1–4); a global concurrency cap prevents
  saturating the network link. At 0.1–2 GB per file, serial transfer with a progress
  bar is the right default.
- **Pause** stops dispatching new work. The in-flight file finishes (with a "cancel
  current" option that deletes the `.part` and leaves the source alone).

### 6.8 State, journal, recovery

`%APPDATA%\MSmover\`:
- `config.json` — rules and settings, written atomically (temp + `File.Replace`),
  with a schema version.
- `journal.jsonl` — append-only: path, size, hash, target, timestamps, outcome.
  Used for the history view, for de-duplication, and on startup to delete orphaned
  `.msmover-part` files at the target.
- `logs/msmover-YYYYMMDD.log` — rolling, 14-day retention.

Optionally, a `msmover_index.tsv` at the target root — the modern replacement for the
old `raw_filelist.txt`.

### 6.9 Dry-run

A global master switch plus a per-rule toggle. The pipeline runs through step 3 and
then logs `WOULD MOVE <src> → <dst>` or the precise skip reason. Nothing is created,
copied, deleted or linked.

A **Preview** button runs a dry pass over the current backlog and shows a table:
`source | verdict | target | reason`. This is how you validate a new rule before
arming it.

## 7. User interface

Tray icon states: idle (grey), transferring (green), paused (amber), error (red).
Context menu: Open · Pause/Resume · Scan now · Dry-run toggle · Quit.

Main window:

| Tab | Contents |
|---|---|
| **Rules** | Grid of rules, enable/disable, add/edit/remove. Editor dialog with all settings and the live filename→target preview. |
| **Queue** | Pending / in-progress / done / failed, per-file progress bar, MB/s and ETA. |
| **Log** | Colour-coded, level filter, text search, "open log folder". |
| **Settings** | Global concurrency, autostart, hash algorithm, rescan interval, log retention, dry-run master switch, symlink capability status. |

Status bar: state, pending count, current throughput. Single-instance mutex; launching
again focuses the existing window.

## 8. Per-rule settings (complete)

`name`, `enabled`, `source`, `target`, `recursive`, `includeRegex`, `excludeRegex`,
`mode` (copy|move), `delimiter`, `expectedDelimiterCount`, `targetTemplate`,
`order` (newest|oldest), `minAgeSeconds`, `stabilityProbes`, `stabilityIntervalSeconds`,
`minSizeBytes`, `requireSiblingGlob`, `createSymlink`, `verifyMode` (hash|size|none),
`hashAlgorithm` (xxhash64|sha256|md5), `onTargetExists` (skip), `maxRetries`,
`retryBackoffSeconds`, `deleteEmptySourceDirs`, `dryRun`, `parallelism`,
`indexFile`, `externalCommand`.

## 9. Build phases

| Phase | Deliverable | Status |
|---|---|---|
| 0 | Repo scaffold: solution, three projects, MIT licence, `.gitignore`, README | done |
| 1 | Config model + atomic persistence; `PathMapper` + unit tests; `StabilityGate` + unit tests | done |
| 2 | `TransferEngine`, hashing, verification, journal, crash recovery + fault-injection tests | done |
| 3 | Watcher, rescan fallback, priority queue, pause | done |
| 4 | WinForms: tray, main window, rule editor with live preview, log and queue views | done |
| 5 | Symlink pre-flight + elevated fix helper; autostart; single instance | done |
| 6 | Packaging: single-file publish, GitHub Actions release build | done (68 MB exe) |
| 7 | Field validation on the instrument: dry run → copy mode → move mode | **yours to do** |

## 9a. What changed during implementation

Four things worth recording, because they were not obvious from the design:

- **`fsutil` output is hyphenated.** The real text is `Local-to-local symbolic link evaluation
  is: ENABLED`, not `Local to local`. More importantly, the probe no longer trusts that parse at
  all: it creates a real symlink and **reads through it**, which is immune to output-format changes
  and to non-English Windows. The `fsutil` values are kept for the diagnostics display only.

- **`ListView.DrawItem` must not set `DrawDefault`.** Doing so tells the control the whole row is
  painted and `DrawSubItem` is then never raised, which silently removed the queue progress bar.

- **`NumericUpDown` validates `Value` against whatever range is set at assignment time**, and the
  defaults are 0..100. `Maximum` and `Minimum` have to be assigned first or the constructor throws.

- **The test harness leaked into `%APPDATA%`.** `TempWorkspace` redirects the static
  `AppPaths.Root`, and xUnit's default per-class parallelism let two workspaces race, so some runs
  wrote to the real state folder. The assembly now runs serially.

Symlink creation could not be exercised on the development machine (Developer Mode off, no
elevation), so `TransferEngine.CreateSymlink` is an internal seam that the tests replace. The
ordering guarantee — link created while the source still exists, source deleted only afterwards —
is covered that way. The real `File.CreateSymbolicLink` path still needs one confirmation run on
the instrument PC.

## 10. Testing

- **Unit** — `PathMapper` against the old script's cases (too few / too many delimiters,
  missing tokens, path traversal, long paths).
- **Acquisition simulator** — a background writer opens a file `FileShare.None` and
  writes 200 MB slowly before closing. Assert MSmover does not touch it until the
  handle is released.
- **Fault injection** — kill mid-copy (assert `.part` cleanup, source intact); corrupt
  the read-back (assert source preserved, error logged); make the target vanish
  mid-transfer; pre-existing target file; symlink privilege absent.
- **Invariant test** — a source file is never deleted unless a verified, correctly
  named target exists.

## 11. Known risks

| Risk | Mitigation |
|---|---|
| `FileSystemWatcher` misses events or overflows | Mandatory periodic rescan + overflow handler |
| SMB symlink evaluation (`L2R`) off by default | Pre-flight check with a one-click elevated fix |
| Instrument briefly releases the file lock mid-run | Min-age + 3× size-stability probes + optional sibling-file guard |
| Antivirus locks the file on the network | Retry with backoff; source untouched throughout |
| Paths over 260 chars | `\\?\` prefix + `longPathAware` manifest |
| Mapped drive not ready at login | Prefer UNC targets; retry rather than fail |
| Self-contained exe ~80 MB | Acceptable; framework-dependent build (~2 MB) also produced as an alternative |
