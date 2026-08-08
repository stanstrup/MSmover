# MSmover

A Windows tray application that watches an LC-MS instrument's acquisition folder and transfers
finished `.raw` files to a network drive — safely, with byte-level verification before anything is
deleted.

Successor to the `watchexec` + batch script in `QC4Metabolomics/file_mover`, adapted from Waters
`.raw` **folders** to Thermo `.raw` **files**.

- Single self-contained `.exe` (~68 MB). No .NET runtime, no installer, no admin to run.
- Tray icon, multiple named rules, dry run, pause, live log.
- **Copy → read back from the destination → compare hashes → only then delete the source.**
- Deletions are never propagated. See [Safety model](#safety-model).

---

## Install

Grab `MSmover.exe` from a release, or build it (see [Building](#building)), and put it anywhere.
First launch creates `%APPDATA%\MSmover\`.

Turn on **Settings → Start MSmover automatically when I log in** to have it come up with the
session. It runs as the logged-in user on purpose: mapped drive letters only exist inside a user
session, and symlink creation uses that user's privileges.

## Configure a rule

**Rules → Add rule…**

| Setting | What it does |
|---|---|
| Source / target folder | Where files come from and go to. UNC paths are fine and preferred over mapped letters. |
| Include regex | Matched against the *file name*. Default `(?i)\.raw$`. |
| Mode | `Copy` never deletes. `Move` deletes the source only after verification. |
| Delimiter + count | Splits the base name into `{t1}`, `{t2}`, …, and optionally rejects names with the wrong number of delimiters. |
| Target template | Where the file lands, relative to the target folder. |
| Order | Newest file first (default) or oldest first. |
| Dry run | Report what would happen, change nothing. **On by default for new rules.** |

The editor shows a **live preview**: type a file name and it resolves the target path, or tells you
exactly why the file would be skipped. Use it before arming anything.

### Template tokens

| Token | Meaning |
|---|---|
| `{t1}` … `{tN}` | base name split on the delimiter, 1-based |
| `{filename}` `{basename}` `{ext}` | name with extension / without / extension only |
| `{relpath}` | sub-folder below the source root (recursive mode) |
| `{yyyy}` `{yy}` `{MM}` `{dd}` `{HH}` `{mm}` `{ss}` | from the file's modified time (case sensitive: `{MM}` month, `{mm}` minute) |
| `{g:name}` | named capture group from the include regex |
| `{machine}` `{rulename}` | host name, rule name |

The old script's Waters layout is the preset:

```
Delimiter            _
Expected delimiters  2
Template             {t1}\{t1}.pro\Data\{filename}

MSTEST_A01_003.raw  ->  MSTEST\MSTEST.pro\Data\MSTEST_A01_003.raw
```

Names with the wrong delimiter count are skipped and logged, exactly as before
(*"Filename check: too few delimiters. File ignored."*).

### Recommended rollout

1. Create the rule with **Dry run** on. Use **Preview (dry pass)…** to see the whole backlog
   mapped out, and check the log for a while.
2. Switch to `Copy` mode for real. Confirm files land where you expect.
3. Only then switch to `Move`.

---

## How "finished writing" is decided

A file is transferred only when **all** of these hold. It is deliberately belt-and-braces: a false
negative costs a few minutes, a false positive costs data.

1. Not a symlink or other reparse point — MSmover never re-processes its own output.
2. At least `MinSizeBytes` (default 1 KiB), so stubs are ignored.
3. Last written at least `MinAgeSeconds` ago (default 60).
4. The optional companion file exists — e.g. `{basename}.sld`. This is the general form of the old
   script's *"no `.IDX` yet means the run has not really started"* guard.
5. Size unchanged across `StabilityProbes` consecutive readings (default 3), spaced
   `StabilityIntervalSeconds` apart (default 10).
6. **The file can be opened with `FileShare.None`.** This is the primary signal: Thermo Xcalibur
   holds the `.raw` open for the whole acquisition. Note that Windows may not even update the
   directory entry's size or modified time until that handle closes, which is why the lock test
   matters more than the size check.

The exclusive-open test is repeated immediately before the transfer starts, and the handle is held
for the entire read, so nothing can append to a file mid-copy.

Discovery uses `FileSystemWatcher` **plus** a periodic full rescan (default every 5 minutes). The
rescan is not optional: watchers drop events on buffer overflow and are unreliable on network
sources. A buffer overflow forces an immediate rescan.

---

## Safety model

The transfer sequence, in order:

```
 1. take an exclusive lock on the source, hold it for the whole read
 2. if a file already exists at the target -> warn, leave everything alone, stop
 3. stream into "<name>.msmover-part", hashing the source in the same pass
 4. flush to the storage device
 5. re-open the part file FROM THE DESTINATION and hash it independently
 6. compare length and hash; on any mismatch delete the part and stop
 7. rename the part to its final name
 8. move mode only: create the symlink, THEN delete the source, THEN rename the link into place
```

Consequences worth stating plainly:

- **No code path deletes a source file that has not been byte-verified at the destination.**
- The `.msmover-part` suffix means an interrupted transfer can never leave a plausible-looking
  complete file at the target. Orphaned part files are recorded in the journal and cleaned up on
  the next launch.
- Symlink creation happens *before* the source is deleted, so a symlink failure leaves the source
  in place rather than leaving the original location empty.
- Killing MSmover mid-transfer is safe. The partial destination file is discarded; the source is
  untouched.
- **Deletions are never propagated.** MSmover is one-way and per-file: it does not enumerate the
  target to reconcile it against the source, and contains no code path that deletes anything in the
  target folder. Mirror and sync semantics are deliberately not implemented — this is a guarantee
  of the design, not a setting that could be flipped by accident.

Verification defaults to xxHash64, which runs at several GB/s and never bottlenecks the transfer.
SHA-256 and MD5 are available if you want a value that means something to other tools. Verification
cannot be disabled in Move mode.

---

## Symlink back to the moved file

With `Move` + **Leave a symbolic link at the original location**, the instrument PC keeps a working
path to the data at its original location.

Two things must be true, and MSmover checks both **before** a rule starts rather than discovering
them after moving a file:

1. **`SeCreateSymbolicLinkPrivilege`** — turn on Developer Mode
   (Settings → System → For developers), or run MSmover as administrator.
2. **Local-to-remote symlink evaluation**, which is disabled by default on Windows and is exactly
   the case that applies to a network target:

   ```
   fsutil behavior set SymlinkEvaluation L2R:1 R2R:1
   ```

   Settings → Symlink capability shows the current state and offers to run this elevated for you.

The pre-flight creates a real link into the real target, **reads through it**, and removes it
again — it does not rely on parsing `fsutil` output. If it fails, the rule refuses to start, the
tray icon goes red, and the log explains what to fix. Nothing is moved in the meantime.

---

## Why not rsync

- Every working rsync on Windows (cwRsync, MSYS2, Git-for-Windows) needs the Cygwin/MSYS runtime,
  which breaks the no-dependencies requirement.
- rsync does not accept UNC paths; it would need `net use Z:` first, which is fragile at login
  before the network is up.
- The delta algorithm buys nothing here. These are brand-new files with no prior version at the
  destination, so rsync degrades to a whole-file copy with a worse error surface.

A native chunked copy plus an independent read-back hash is a *stronger* guarantee than rsync
without `--checksum`.

If you want an external copier anyway, set **External copy command** on a rule
(`{src}` and `{dst}` placeholders, e.g. `robocopy` or an rsync binary). Verification still runs
afterwards, so the safety properties above are preserved.

---

## Files it keeps

```
%APPDATA%\MSmover\config.json        rules and settings (atomic writes)
%APPDATA%\MSmover\journal.jsonl      every transfer: source, target, size, hash, outcome
%APPDATA%\MSmover\logs\              rolling daily logs, 14-day retention by default
```

The journal is also how the app knows what it has already transferred, so restarting does not
re-report every file it has ever copied. **Scan now** clears that and re-evaluates everything.

Set **Index file** on a rule to also append a TSV at the target root — the successor to the old
`raw_filelist.txt`.

---

## Building

Requires the .NET 8 SDK.

```powershell
dotnet test  tests\MSmover.Core.Tests\MSmover.Core.Tests.csproj

dotnet publish src\MSmover.App\MSmover.App.csproj -c Release -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o publish\win-x64
```

Layout:

```
src/MSmover.Core   engine: config, naming, detection, transfer, journal   (no UI, fully testable)
src/MSmover.App    WinForms: tray, main window, rule editor, preview
tests/             xUnit, including an acquisition simulator and fault injection
```

The only runtime package is `System.IO.Hashing` (Microsoft, for xxHash64).

## Licence

MIT. See [LICENSE](LICENSE).
