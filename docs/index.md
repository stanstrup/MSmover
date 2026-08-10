---
title: MSmover
---

# MSmover

**Watches an LC-MS instrument's acquisition folder and transfers finished `.raw` files to a
network drive — safely, with byte-level verification before anything is deleted.**

A single self-contained `.exe`. No .NET runtime, no installer, no admin rights to run.

<div class="row">
<div class="col-md-6">

### Start here

* [Getting started](articles/getting-started.md) — install, first rule, safe rollout
* [Rules explained](articles/rules.md) — every setting and what it changes
* [When is a file finished?](articles/completion.md) — the six checks
* [Safety model](articles/safety.md) — what guarantees you actually get

</div>
<div class="col-md-6">

### Cookbooks

* [Regex cookbook](cookbooks/regex.md) — patterns for selecting files
* [Template cookbook](cookbooks/templates.md) — filename → destination path
* [Symlinks](articles/symlinks.md) — links back to moved data, and clearing them
* [Troubleshooting](articles/troubleshooting.md) — why a file is not moving

</div>
</div>

---

## The one thing worth knowing

Every transfer runs this sequence, in this order:

```text
 1. take an exclusive lock on the source, hold it for the whole read
 2. if a file already exists at the target -> warn, leave everything alone, stop
 3. stream into "<name>.msmover-part", hashing the source in the same pass
 4. flush to the storage device
 5. re-open the part file FROM THE DESTINATION and hash it independently
 6. compare length and hash; on any mismatch delete the part and stop
 7. rename the part to its final name
 8. move mode only: create the symlink, THEN delete the source, THEN rename the link
```

> [!IMPORTANT]
> No code path deletes a source file that has not been byte-verified at the destination, and no
> code path deletes anything in the target folder at all. Deletion propagation is absent by
> design, not disabled by a setting you could flip by accident.

## Feature summary

| | |
|---|---|
| **Interface** | Tray icon plus a window with Rules, Queue, Log and Settings tabs |
| **Rules** | As many as you like, each independently enabled, ordered and dry-runnable |
| **Detection** | Exclusive-open test, size stability, minimum age, optional companion file, plus a periodic full rescan behind `FileSystemWatcher` |
| **Modes** | Copy (never deletes) or Move (deletes only after verification) |
| **Routing** | Template with `{t1}`-style tokens, date tokens and regex capture groups |
| **Selection** | .NET regex include and exclude, matched against the file name |
| **Verification** | xxHash64 (default), SHA-256 or MD5, read back from the destination |
| **Symlink back** | Real symbolic link left at the original location, with a pre-flight capability check |
| **Ordering** | Newest file first (default) or oldest first |
| **Dry run** | Per rule and globally, plus a whole-backlog preview table |
| **Recovery** | Journalled; orphaned partial files are cleaned up on the next launch |

## Where things live

```text
%APPDATA%\MSmover\config.json        rules and settings (atomic writes)
%APPDATA%\MSmover\journal.jsonl      every transfer: source, target, size, hash, outcome
%APPDATA%\MSmover\logs\              rolling daily logs
```

## Licence

MIT.
