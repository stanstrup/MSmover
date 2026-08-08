# Settings reference

Every setting, its JSON name in `config.json`, and its default. See
[Rules explained](../articles/rules.md) for the prose version.

## Application settings

Top level of `config.json`. Edited under **Settings**.

| JSON | GUI | Type | Default | Meaning |
|---|---|---|---|---|
| `SchemaVersion` | — | int | `1` | Config format version. |
| `GlobalDryRun` | Global dry run | bool | `false` | Master override. No rule may write, copy or delete anything. |
| `Paused` | Pause | bool | `false` | Survives a restart, so a deliberate pause is not undone by a reboot. |
| `StartMinimised` | Start minimised to the tray | bool | `true` | |
| `AutoStartWithWindows` | Start automatically when I log in | bool | `false` | Backed by `HKCU\…\Run`; the registry value is the source of truth. |
| `GlobalMaxConcurrentTransfers` | Max concurrent transfers | int | `2` | Ceiling across all rules. |
| `LogRetentionDays` | Keep log files for | int | `14` | |
| `LogLevel` | Level | `Debug`\|`Info`\|`Warn`\|`Error` | `Info` | |
| `CopyChunkBytes` | Copy buffer | int | `1048576` | 1 MiB. Raise for high-latency shares. |
| `Rules` | — | array | `[]` | |

## Rule settings

One object per entry in `Rules`.

### Identity

| JSON | GUI | Type | Default |
|---|---|---|---|
| `Id` | — | string | generated GUID |
| `Name` | Name | string | `New rule` |
| `Enabled` | Enabled | bool | `false` |
| `DryRun` | Dry run | bool | **`true`** |

### Folders and mode

| JSON | GUI | Type | Default |
|---|---|---|---|
| `SourceFolder` | Source folder | string | `""` |
| `TargetFolder` | Target folder | string | `""` |
| `Recursive` | Include sub-folders | bool | `false` |
| `Mode` | Mode | `Copy`\|`Move` | `Copy` |

### Selection

| JSON | GUI | Type | Default |
|---|---|---|---|
| `IncludeRegex` | Include regex | string | `"(?i)\\.raw$"` |
| `ExcludeRegex` | Exclude regex | string | `""` |
| `MinSizeBytes` | Minimum size | long | `1024` |

> [!IMPORTANT]
> In JSON, backslashes are doubled. A pattern typed as `(?i)\.raw$` in the GUI is stored as
> `"(?i)\\.raw$"`.

### Completion detection

| JSON | GUI | Type | Default |
|---|---|---|---|
| `MinAgeSeconds` | Minimum age | int | `60` |
| `StabilityProbes` | Stability probes | int | `3` |
| `StabilityIntervalSeconds` | Probe interval | int | `10` |
| `RequireSiblingGlob` | Required companion file | string | `""` |

### Routing

| JSON | GUI | Type | Default |
|---|---|---|---|
| `Delimiter` | Delimiter | string | `"_"` |
| `ExpectedDelimiterCount` | Require exactly N delimiters | int? | `null` (off) |
| `TargetTemplate` | Target template | string | `"{filename}"` |
| `DateTokenSource` | Date tokens from | `FileModified`\|`Now` | `FileModified` |

### Transfer and safety

| JSON | GUI | Type | Default |
|---|---|---|---|
| `VerifyMode` | Verification | `Hash`\|`Size`\|`None` | `Hash` |
| `HashAlgorithm` | Hash algorithm | `XxHash64`\|`Sha256`\|`Md5` | `XxHash64` |
| `OnTargetExists` | If the target exists | `Skip` | `Skip` |
| `CreateSymlink` | Leave a symbolic link | bool | `false` |
| `MaxRetries` | Max retries | int | `5` |
| `RetryBackoffSeconds` | Retry backoff | int | `30` |

### Scheduling and extras

| JSON | GUI | Type | Default |
|---|---|---|---|
| `Order` | Order | `NewestFirst`\|`OldestFirst` | `NewestFirst` |
| `Parallelism` | Parallel transfers | int | `1` |
| `RescanSeconds` | Full rescan every | int | `300` |
| `DeleteEmptySourceDirs` | Delete empty source folders | bool | `false` |
| `IndexFile` | Index file | string | `""` |
| `ExternalCommand` | External copy command | string | `""` |

## Validation

A rule is checked before it can be enabled. These are refusals, not warnings:

* name, source, target or template empty
* an invalid include or exclude regex
* `MinAgeSeconds` negative; `StabilityProbes` < 1; `StabilityIntervalSeconds` < 1
* `Parallelism` outside 1–8; `MaxRetries` negative
* a delimiter count set with no delimiter
* **`Mode = Move` with `VerifyMode = None`** — the source would be deleted unverified
* source and target the same folder
* target inside the source folder while `Recursive` is on

## Template tokens

| Token | Meaning |
|---|---|
| `{t1}` … `{tN}` | base name split on the delimiter, 1-based |
| `{filename}` `{basename}` `{ext}` | name with extension / without / extension only |
| `{relpath}` | sub-folder below the source root |
| `{yyyy}` `{yy}` `{MM}` `{dd}` `{HH}` `{mm}` `{ss}` | from `DateTokenSource`; **case-sensitive** |
| `{g:name}` | capture group from the include regex; a position also works, e.g. `{g:1}` |
| `{machine}` `{rulename}` | host name, rule name |

## Item states

Shown in the Queue tab.

| State | Terminal | Meaning |
|---|---|---|
| `Pending` | no | Discovered, not yet evaluated |
| `Waiting` | no | Not finished being written; *Detail* names the check |
| `Ready` | no | All checks passed, queued for a slot |
| `Transferring` | no | In flight |
| `Done` | yes | Transferred and verified |
| `Blocked` | yes | A file already exists at the target |
| `Failed` | yes | Retries exhausted; source untouched |
| `Skipped` | yes | The name failed the naming rule |

## Mapping verdicts

| Verdict | Meaning |
|---|---|
| `Ok` | Resolved |
| `NotIncluded` | Include regex did not match; the file is not queued at all |
| `Excluded` | Exclude regex matched |
| `TooFewDelimiters` / `TooManyDelimiters` | Wrong delimiter count |
| `UnknownToken` | Unknown token, out-of-range `{tN}`, or a missing capture group |
| `EmptyToken` | A token resolved to an empty string |
| `InvalidPath` | Illegal characters, trailing dot or space, `.`/`..`, or escapes the target folder |

## Command line

| Argument | Effect |
|---|---|
| `--tray` | Start hidden in the tray. Used by the autostart entry. |

MSmover is single-instance: launching a second copy brings the running one to the front.
