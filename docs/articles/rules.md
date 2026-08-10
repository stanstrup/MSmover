# Rules explained

A rule is one source folder, one target folder, and everything about how files travel between them.
You can have as many as you like — one per instrument, or several on one instrument routing
different projects differently.

Rules are edited in **Rules → Add rule… / Edit…** and stored in `%APPDATA%\MSmover\config.json`.

## Rule

| Setting | Default | Notes |
|---|---|---|
| **Name** | — | Appears in the log and the Queue tab. Also available as `{rulename}`. Changing it resets the "already transferred" memory, which is keyed on the name. |
| **Enabled** | off | New rules start disabled on purpose. |
| **Dry run** | **on** | Report only. Overridden by global dry run, never the other way round. |

## Folders

| Setting | Default | Notes |
|---|---|---|
| **Source folder** | — | Where the instrument writes. |
| **Target folder** | — | UNC paths (`\\server\share\…`) are preferred over mapped letters: a mapped drive only exists inside a logged-in session and may not be ready at login. |
| **Include sub-folders** | off | Turns on recursive discovery and makes `{relpath}` meaningful. |
| **Mode** | `Copy` | `Copy` never deletes. `Move` deletes the source, but only after verification. |

> [!WARNING]
> A target inside the source folder with recursion on is rejected by validation — the rule would
> feed itself its own output.

## Which files

| Setting | Default | Notes |
|---|---|---|
| **Include regex** | `(?i)\.raw$` | Matched against the file *name*. Empty means everything. See the [regex cookbook](../cookbooks/regex.md). |
| **Exclude regex** | empty | Wins over include. |
| **Minimum size** | 1024 bytes | Skips stubs. |

## When is a file finished

Covered in full in [When is a file finished?](completion.md).

| Setting | Default |
|---|---|
| **Minimum age** | 60 s |
| **Stability probes** | 3 |
| **Probe interval** | 10 s |
| **Required companion file** | empty |

## Where it goes

| Setting | Default | Notes |
|---|---|---|
| **Delimiter** | `_` | Splits the base name into `{t1}`, `{t2}`, … |
| **Require exactly N delimiters** | off | Rejects malformed names before they are filed. |
| **Target template** | `{filename}` | See the [template cookbook](../cookbooks/templates.md). |
| **Date tokens from** | `FileModified` | Or `Now`, to stamp with the transfer time instead. |

## Safety

| Setting | Default | Notes |
|---|---|---|
| **Verification** | `Hash` | `Hash` reads the destination back and compares. `Size` compares length only. `None` is rejected in Move mode. |
| **Hash algorithm** | `XxHash64` | Several GB/s. `Sha256` and `Md5` available when the value needs to mean something elsewhere. |
| **If the target exists** | Skip and warn | The only policy. There is deliberately no overwrite option. |
| **Leave a symbolic link** | off | Move mode only. Pre-flighted before the rule starts — see [Symlinks](symlinks.md). |
| **Max retries** | 5 | A locked source does not count against this. |
| **Retry backoff** | 30 s | Multiplied by the attempt number, so 30 s, 60 s, 90 s… |

## Other

| Setting | Default | Notes |
|---|---|---|
| **Order** | `NewestFirst` | The run you are waiting for moves before the backlog. |
| **Parallel transfers** | 1 | Also capped by **Max concurrent transfers** in Settings. |
| **Full rescan every** | 300 s | The fallback behind `FileSystemWatcher`. Not optional; the watcher drops events. |
| **Delete empty source folders** | off | Move mode, recursive. Never removes the source root itself. |
| **Index file** | empty | A TSV of completed transfers, appended at the target root. |
| **External copy command** | empty | Run something else as the copier — see below. |

## The external copy command

An escape hatch, not the normal path. Set it to a command with `{src}` and `{dst}` placeholders:

```text
robocopy "{src}" "{dst}" /NFL /NJS /NDL
```

MSmover releases its lock, runs the command via `cmd.exe`, then **still performs its own
verification** on the result — so the safety guarantees hold whatever the external tool did.
`robocopy` exit codes below 8 are treated as success, per its convention; everything else requires
exit code 0.

There is a cost: with an external copier MSmover cannot hash the source during the copy, so it
reads the source a second time. Use the built-in copier unless you have a specific reason not to.

## Reading the Rules tab

| Column | Meaning |
|---|---|
| *State* | `Running`, `Paused`, `Disabled`, or `Faulted` — with the reason in *Detail* |
| *Mode* | e.g. `Move (dry run) +link` |
| *Pending* | Files discovered and not yet finished |
| *Detail* | Fault reason, when there is one |

Grey means disabled, amber paused, red faulted.

## Buttons

| Button | Does |
|---|---|
| **Add rule… / Edit…** | Opens the editor. Double-clicking a row also edits. |
| **Duplicate** | Copies a rule, disabled and in dry run. The quick way to add a second instrument. |
| **Remove** | Deletes the rule. No files are affected. |
| **Enable / disable** | Toggles. Validates first, and asks for confirmation before arming a real Move. |
| **Preview (dry pass)…** | Walks the source folder now and shows what would happen to each file. Read-only. |
| **Clear symlinks…** | Lists the symbolic links under the source folder and lets you remove them. See [Symlinks](symlinks.md#clearing-links-out-again). |
