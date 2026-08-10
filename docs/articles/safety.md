# Safety model

MSmover exists because "it is very important no data is lost". This page states exactly what is
guaranteed, and what is not.

## The transfer sequence

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

## What is guaranteed

**A source file is never deleted unless a verified, correctly named copy exists at the
destination.** There is no code path that reaches step 8 without step 6 having passed.

**Nothing in the target folder is ever deleted or overwritten.** MSmover does not enumerate the
target to reconcile it against the source, and contains no code that deletes anything there. The
only file it removes from the destination is its own `.msmover-part` after a failure. Deletion
propagation is *absent by design*, not disabled by a setting that could be flipped by accident —
mirror and sync semantics are simply not implemented.

**An interrupted transfer cannot leave a plausible-looking complete file at the destination.** The
copy is written to `<name>.msmover-part` and renamed only after verification, so a file present
under its final name is always a verified file. Orphaned part files are recorded in the journal and
deleted on the next launch.

**Killing MSmover mid-transfer is safe.** The partial destination file is discarded and the source
is untouched. Quitting from the tray warns you first.

**A symlink failure cannot leave the original location empty.** In move mode the link is created
while the source still exists; only then is the source deleted, and the link renamed into its
place. If link creation fails, the source stays and the (verified) copy is at the destination —
you get a loud log entry and a Blocked item, not a hole.

**A file name cannot escape the target folder.** The resolved path is checked for containment
before anything is created, so a name containing `..` is rejected rather than obeyed.

## What is not guaranteed

**Bit rot after the fact.** Verification proves the bytes arrived intact. It says nothing about the
storage a month later. MSmover records the hash in `journal.jsonl` and optionally in the index file
so you *can* check later — but it does not check for you.

**That the file was complete when the instrument released it.** MSmover can only detect that
nothing holds the file open and its size has settled. If acquisition software crashes halfway
through and closes its handle, that truncated file looks finished. Nothing outside the vendor's
format can tell the difference.

**Protection against a wrong rule.** A template that routes files somewhere useless will do so
faithfully. This is what dry run and the preview are for.

**Atomicity across a network interruption between steps 7 and 8.** If the machine loses power in
the microseconds between the rename and the delete, you get the file in both places — the safe
failure direction, and the next scan reports it as blocked.

## Verification

| Setting | Behaviour |
|---|---|
| `Hash` (default) | Copy, then re-read the destination over the network and compare hashes. Catches silent corruption in transit. |
| `Size` | Compare byte length only. Faster, catches truncation, misses corruption. |
| `None` | No verification. **Rejected in Move mode** by rule validation. |

Hash algorithm defaults to **xxHash64**: non-cryptographic, several GB/s, and never the bottleneck
on a 1 GB file. SHA-256 and MD5 are available when you want a value that means something to an
auditor or another tool. For detecting accidental corruption they are all equivalent; xxHash64 is
simply faster.

The read-back is a genuine second read from the destination, not a cached buffer — `Flush(true)`
forces the write to the device first.

## Layers of "do not do anything yet"

Four independent brakes, any one of which stops writes:

| Brake | Scope | Default |
|---|---|---|
| Rule not **Enabled** | one rule | new rules start disabled |
| Rule **Dry run** | one rule | on for new rules |
| **Global dry run** | everything | off |
| **Pause** | everything | off, and survives a restart |

Dry run runs the whole pipeline up to step 2 and then logs what it *would* have done. Nothing is
created, copied, deleted or linked. Turning global dry run off asks for confirmation; enabling a
Move rule with dry run off asks again.

## Collisions

If a file already exists at the resolved target, MSmover logs a warning, leaves both files exactly
as they are, and marks the item **Blocked**. This is the only policy implemented — there is
deliberately no overwrite option.

Resolve it yourself, then press **Scan now** to re-evaluate.

## Failure handling

| Outcome | Retries? | Source |
|---|---|---|
| Source locked | yes, not counted against the budget | untouched |
| Hash or length mismatch | yes, up to `MaxRetries` | untouched |
| Target folder unreachable | yes, with backoff | untouched |
| Target file exists | no — terminal, needs a human | untouched |
| Name fails the naming rule | no — terminal | untouched |
| Retries exhausted | no — terminal, logged as an error | untouched |

In every one of these the source is untouched. That is the invariant the test suite exists to
protect: see `TransferEngineTests` and `SymlinkOrderingTests`, which inject corrupted copies,
cancellations, pre-existing targets and symlink failures and assert the source survives each one.

## Audit trail

`%APPDATA%\MSmover\journal.jsonl` records every transfer as one JSON object per line: source,
target, part path, size, hash, mode, outcome, timestamp. It rotates at 20 MB.

```json
{"Ts":"2026-03-14T15:12:03.4+01:00","Event":"done","Rule":"Thermo raw","Source":"D:\\Data\\MSTEST_A01_003.raw","Target":"\\\\storage\\ms\\MSTEST\\MSTEST.pro\\Data\\MSTEST_A01_003.raw","Size":1288490188,"Hash":"b2baad2182f1b1ab","Mode":"MOVE"}
```

One object per line, so it streams into any JSON-lines reader without loading the whole file.
