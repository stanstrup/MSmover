# Why not rsync

A reasonable question, since rsync is the obvious tool for "get files from here to there reliably".
It was evaluated and rejected. Here is the reasoning, so you can disagree with it on the merits.

## It cannot be shipped dependency-free

Every working rsync on Windows is a POSIX emulation build:

| Build | Ships as |
|---|---|
| cwRsync | needs the Cygwin runtime |
| MSYS2 `rsync` | needs `msys-2.0.dll` and friends |
| Git for Windows' rsync | same MSYS2 runtime |
| `openrsync` | no maintained Windows port |

The hard requirement was a single `.exe` on a locked-down instrument PC with no runtime to install.
Pure-Rust and Go reimplementations (`oc-rsync`, `resync`, `rsync-go`) do produce static binaries,
but they are young and unproven for data you cannot re-acquire.

## It cannot take UNC paths

rsync treats `\\server\share` as a path with escape characters, not a UNC path. Using it against a
Windows share means mapping a drive letter first:

```text
net use Z: \\storage\ms
```

Mapped drives live in a user session and are frequently not ready at login, which is exactly when a
folder watcher starts. That is a fragile foundation for the one job that must not fail.

## The delta algorithm buys nothing here

rsync's value is transferring a file that already exists at the destination in an older form: it
sends only the differences. MSmover's files are **new acquisitions with no prior version at the
destination**, so rsync degrades to a whole-file copy — with a larger error surface and a worse
story for reporting what went wrong.

## Plain rsync is also weaker on verification

By default rsync verifies with size and modification time, not content. `--checksum` changes what
it uses to *decide whether to transfer*, and the whole-file MD4 rolling check protects the transfer
itself, but neither is the same as what MSmover does:

> copy, then re-open the destination file from the network and hash it independently, and only
> then delete the source.

That is a stronger guarantee, and it is the guarantee the requirement asked for.

## What is used instead

A chunked streaming copy that hashes the source in the same pass, followed by an independent
read-back and hash of the destination. See [Safety model](safety.md).

## If you want rsync anyway

Set **External copy command** on the rule:

```text
rsync -a --inplace "{src}" "{dst}"
```

or, more usefully on Windows:

```text
robocopy "{src}" "{dst}" /NFL /NJS /NDL
```

MSmover runs it and then applies its own verification to the result, so the safety properties above
still hold. See [Rules → the external copy command](rules.md#the-external-copy-command) for the
trade-off.
