# MSmover

A Windows tray application that watches an LC-MS instrument's acquisition folder and transfers
finished `.raw` files to a network drive — safely, with byte-level verification before anything is
deleted.

- Single self-contained `.exe` (~68 MB). No .NET runtime, no installer, no admin to run.
- Tray icon, multiple named rules, dry run, pause, live log.
- **Copy → read back from the destination → compare hashes → only then delete the source.**
- Deletions are never propagated. See [Safety model](#safety-model).

📖 **[Documentation](https://stanstrup.github.io/MSmover/)** ·
📦 **[Download](https://github.com/stanstrup/MSmover/releases/latest)**

## Install

From the [latest release](https://github.com/stanstrup/MSmover/releases/latest), either:

- **`MSmover-<version>-win-x64-setup.exe`** — installer. Per-user, so no administrator rights:
  it installs to `%LOCALAPPDATA%\Programs\MSmover`, adds a Start Menu entry and an uninstaller,
  and offers to start MSmover at login.
- **`MSmover-<version>-win-x64.exe`** — the portable executable. Put it anywhere and run it.

Both are the same application; the installer just wraps it. Neither needs a .NET runtime.

Windows SmartScreen will warn about an unrecognised publisher because the binaries are not
code-signed. Each release publishes a `.sha256` alongside every download:

```powershell
(Get-FileHash MSmover-0.1.0-win-x64-setup.exe -Algorithm SHA256).Hash
```

Upgrading keeps your rules and history — they live in `%APPDATA%\MSmover\`, which the installer
never touches and the uninstaller only removes if you say yes.

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

For example:

```
Delimiter            _
Expected delimiters  2
Template             {t1}\{t1}.pro\Data\{filename}

MSTEST_A01_003.raw  ->  MSTEST\MSTEST.pro\Data\MSTEST_A01_003.raw
```

Names with the wrong delimiter count are skipped and logged
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

## Clearing symlinks out again

**Rules → Clear symlinks…** lists every symbolic link under a rule's source folder, with what each
one points at, and removes the ones you tick. Filters for "only links into this rule's target" and
"only broken links" make it usable both for routine tidying and for cleaning up after a share was
reorganised.

It only ever deletes reparse points — re-checked immediately before each delete — and removing a
link never touches the file it points at.

---

## Files it keeps

```
%APPDATA%\MSmover\config.json        rules and settings (atomic writes)
%APPDATA%\MSmover\journal.jsonl      every transfer: source, target, size, hash, outcome
%APPDATA%\MSmover\logs\              rolling daily logs, 14-day retention by default
```

The journal is also how the app knows what it has already transferred, so restarting does not
re-report every file it has ever copied. **Scan now** clears that and re-evaluates everything.

Set **Index file** on a rule to also append a TSV of completed transfers at the target root.

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
docs/              docfx site: articles, cookbooks, reference, generated API
```

The only runtime package is `System.IO.Hashing` (Microsoft, for xxHash64).

### Documentation

The site is [docfx](https://dotnet.github.io/docfx/) — the same split as pkgdown: hand-written
articles plus a reference generated from the `///` comments in `MSmover.Core`.

```powershell
dotnet tool restore                      # docfx is pinned in .config/dotnet-tools.json
dotnet docfx docs/docfx.json --serve     # http://localhost:8080
```

Every pattern and template printed in the regex and template cookbooks is asserted by
`tests/MSmover.Core.Tests/DocumentationExamplesTests.cs`, and CI runs those before publishing — so
an example that stops being true breaks the build instead of quietly misleading someone.

## Licence

MIT. See [LICENSE](LICENSE).
