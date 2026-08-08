# API reference

Generated from the XML documentation comments in `MSmover.Core` — the engine, with no UI
dependency. Useful if you want to embed the transfer logic somewhere else, or simply to read how a
particular guarantee is implemented.

## Where to start

| Namespace | What lives there |
|---|---|
| @MSmover.Core.Config | `AppConfig`, `RuleConfig`, the enums, and atomic load/save |
| @MSmover.Core.Naming | `PathMapper` — filename to destination path, and every rejection verdict |
| @MSmover.Core.Detection | `StabilityGate` — the six completion checks |
| @MSmover.Core.Transfer | `TransferEngine` (the safety-critical sequence), hashing, `SymlinkService` |
| @MSmover.Core.Engine | `MoverService`, `RuleRunner`, the queue and autostart |
| @MSmover.Core.Journal | `TransferJournal` — the audit trail and crash recovery |
| @MSmover.Core.Logging | `LogHub` — rolling files plus the in-memory ring the UI polls |
| @MSmover.Core.Common | `FileGuard` (the exclusive-open probe), `LongPath`, `AppPaths` |

## The three types worth reading

**@MSmover.Core.Transfer.TransferEngine** — the ordered sequence that makes the safety guarantees
true. `ExecuteAsync` is the whole story in one method.

**@MSmover.Core.Naming.PathMapper** — pure string logic, no I/O, which is why the rule editor can
run it live against a name you are still typing.

**@MSmover.Core.Detection.StabilityGate** — the completion checks, in the order they are applied.

## A note on the UI

`MSmover.App` is not documented here. It is a thin WinForms shell: it polls
@MSmover.Core.Engine.MoverService for snapshots and never touches the filesystem itself. All the
behaviour worth reading about is in `MSmover.Core`.
