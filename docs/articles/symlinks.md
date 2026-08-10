# Symlinks back to moved files

With **Mode = Move** and **Leave a symbolic link at the original location**, the instrument PC keeps
a working path to data that now lives on the network. Software that opens
`D:\Xcalibur\Data\MSTEST_A01_003.raw` keeps working; the bytes come off the share.

## Two things must be true

Both are checked *before* a rule starts, not discovered after a file has been moved.

### 1. `SeCreateSymbolicLinkPrivilege`

Creating a symbolic link on Windows is a privileged operation. Either:

* turn on **Developer Mode** — Settings → System → For developers, or
* run MSmover **as administrator**.

Developer Mode is the better choice on an instrument PC: it is a one-off machine setting and does
not mean running a file mover elevated all day.

### 2. Local-to-remote symlink evaluation

Windows decides separately whether it will *follow* a link, based on where the link and its target
live. Local-to-remote — a link on `D:` pointing at `\\storage\…` — is **disabled by default**, and
is exactly the case that applies here.

```text
fsutil behavior query SymlinkEvaluation

Local-to-local symbolic link evaluation is: ENABLED
Local-to-remote symbolic link evaluation is: DISABLED   <- this one
Remote-to-local symbolic link evaluation is: DISABLED
Remote-to-remote symbolic link evaluation is: DISABLED
```

Enable it once, from an elevated prompt:

```text
fsutil behavior set SymlinkEvaluation L2R:1 R2R:1
```

**Settings → Symlink capability** shows the current state and offers to run this for you (Windows
will prompt for administrator approval).

> [!NOTE]
> This is a machine-wide setting affecting how Windows resolves *all* symlinks to remote targets,
> not just MSmover's. It is the standard configuration for this pattern, but it is a change to the
> machine and worth knowing you made.

## The pre-flight

When a rule with symlinks enabled starts, MSmover:

1. writes a small probe file into the **target** folder,
2. creates a real symbolic link to it in the **source** folder,
3. confirms the link is a reparse point,
4. **reads through the link** and checks it returns the probe file's contents,
5. deletes both.

Step 4 is the authoritative test. It does not depend on parsing `fsutil` output, so it is immune to
output-format changes, non-English Windows and group policy that is not visible from the registry.

If any step fails the rule **refuses to start**: the tray icon turns red, the rule shows as
`Faulted`, and the log explains what to fix. Nothing is moved in the meantime. This is the whole
point — the alternative is discovering the problem after a file has been moved and the link cannot
be created.

```text
ERROR  Move with symlink | Rule not started - symlink pre-flight failed. Could not create a
                           symlink: A required privilege is not held by the client.
ERROR  Move with symlink |   Creating symbolic links needs SeCreateSymbolicLinkPrivilege. Either:
ERROR  Move with symlink |     - turn on Developer Mode (Settings > System > For developers), or
ERROR  Move with symlink |     - run MSmover as administrator.
ERROR  Move with symlink |   Developer Mode is currently OFF; this process is not elevated.
```

In dry run the pre-flight only warns, so you can preview a rule on a machine that is not set up yet.

## Ordering during a move

```text
1. copy and verify                       (target now has a good copy)
2. create the link at "<source>.msmover-link"
3. delete the source file
4. rename the link to the source's original name
```

The link is created **before** the source is deleted. If step 2 fails, the source is still there
and the verified copy is at the target — you get a loud error, and the file shows as Blocked on the
next scan. You never end up with an empty original location.

## Living with symlinks

**MSmover never re-processes its own links.** The completion gate drops reparse points, so a linked
file is not queued again.

**Writing through a link writes over SMB.** Reading archived data is the intended use. If something
later opens the path for writing, it will be writing to the share.

**Deleting the link does not delete the data.** It removes only the link. Conversely, deleting the
file on the share leaves a dangling link behind — see [clearing links out
again](#clearing-links-out-again).

**Backup software may or may not follow them.** Check before assuming the instrument PC's backup
still covers the data — usually it should not, since the point is that the archive is now on the
network.

---

## Clearing links out again

Links accumulate. Eventually you want the instrument PC's folders genuinely empty — the archive is
on the share, the links have served their purpose, and a few thousand of them make the folder
tedious to work with. Or a share was reorganised and half the links now dangle.

**Rules → select the rule → Clear symlinks…**

The dialog lists every symbolic link under the rule's source folder, with what each one points at,
and lets you delete the ones you tick.

| Filter | Effect |
|---|---|
| Only links pointing into this rule's target folder | On by default. Leaves alone any link that was not created by this rule. |
| Only broken links (target missing) | The tidy-up-after-a-reorganisation case. Broken links are also shown in red. |
| Include sub-folders | Defaults to the rule's own recursive setting. |

Two safety properties, both of them checked rather than assumed:

* **It only ever deletes reparse points**, and it re-tests that attribute immediately before each
  delete. A path that stopped being a link between the scan and the click is skipped and reported,
  not deleted. A real data file can never be removed by this tool.
* **Deleting a link never touches what it points at.** Directory links are removed
  non-recursively, so the link goes and the folder it referred to does not.

Everything removed is written to the log with a count, and anything that could not be removed is
listed with the reason.

> [!TIP]
> Run it once with **Only broken links** ticked before doing anything else. If that finds a lot,
> something moved on the share and it is worth understanding what before clearing the rest.

## If you would rather not

Symlinks are optional. Move mode without them simply leaves the source folder empty, which is often
what you want. The [index file](../cookbooks/templates.md#combining-with-the-index-file) and
`journal.jsonl` both record where every file went, so nothing is lost track of either way.
