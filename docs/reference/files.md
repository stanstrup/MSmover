# Files and formats

```text
%APPDATA%\MSmover\
├── config.json          rules and settings
├── journal.jsonl        every transfer, one JSON object per line
├── journal.jsonl.1      previous journal, after rotation at 20 MB
└── logs\
    └── msmover-YYYYMMDD.log
```

Open either folder from **Settings → Files and folders**.

## `config.json`

Written atomically: a temp file is created alongside and then swapped in, so a crash mid-write can
never leave a truncated config and lose every rule. Enums are stored by name, not number.

```json
{
  "SchemaVersion": 1,
  "GlobalDryRun": false,
  "Paused": false,
  "StartMinimised": true,
  "AutoStartWithWindows": false,
  "GlobalMaxConcurrentTransfers": 2,
  "LogRetentionDays": 14,
  "LogLevel": "Info",
  "CopyChunkBytes": 1048576,
  "Rules": [
    {
      "Id": "8f14e45fceea167a5a36dedd4bea2543",
      "Name": "Thermo raw",
      "Enabled": true,
      "SourceFolder": "D:\\Xcalibur\\Data",
      "TargetFolder": "\\\\storage\\ms\\incoming",
      "Recursive": false,
      "IncludeRegex": "(?i)\\.raw$",
      "ExcludeRegex": "(?i)^(blank|wash)_",
      "Mode": "Move",
      "Delimiter": "_",
      "ExpectedDelimiterCount": 2,
      "TargetTemplate": "{t1}\\{t1}.pro\\Data\\{filename}",
      "DateTokenSource": "FileModified",
      "MinAgeSeconds": 60,
      "StabilityProbes": 3,
      "StabilityIntervalSeconds": 10,
      "MinSizeBytes": 1024,
      "RequireSiblingGlob": "",
      "Order": "NewestFirst",
      "VerifyMode": "Hash",
      "HashAlgorithm": "XxHash64",
      "OnTargetExists": "Skip",
      "MaxRetries": 5,
      "RetryBackoffSeconds": 30,
      "Parallelism": 1,
      "CreateSymlink": true,
      "DeleteEmptySourceDirs": false,
      "IndexFile": "msmover_index.tsv",
      "ExternalCommand": "",
      "DryRun": false,
      "RescanSeconds": 300
    }
  ]
}
```

> [!CAUTION]
> Editing this file while MSmover is running will have your changes overwritten the next time it
> saves. Quit first, or make the change in the GUI.

Note the doubled backslashes: they are JSON escapes, in both paths and regexes.

If the file cannot be parsed, MSmover starts with **no rules** and says so in a dialog. Your file is
not modified — fix or remove it and restart.

## `journal.jsonl`

One JSON object per line. This is the audit trail, and also how MSmover knows what it has already
transferred so a restart does not re-report everything.

```json
{"Ts":"2026-03-14T15:12:01.1+01:00","Event":"start","Rule":"Thermo raw","Source":"D:\\Xcalibur\\Data\\MSTEST_A01_003.raw","Target":"\\\\storage\\ms\\incoming\\MSTEST\\MSTEST.pro\\Data\\MSTEST_A01_003.raw","Part":"\\\\storage\\ms\\incoming\\MSTEST\\MSTEST.pro\\Data\\MSTEST_A01_003.raw.msmover-part","Size":1288490188,"Mode":"MOVE"}
{"Ts":"2026-03-14T15:12:03.4+01:00","Event":"done","Rule":"Thermo raw","Source":"D:\\Xcalibur\\Data\\MSTEST_A01_003.raw","Target":"\\\\storage\\ms\\incoming\\MSTEST\\MSTEST.pro\\Data\\MSTEST_A01_003.raw","Size":1288490188,"Hash":"b2baad2182f1b1ab","Mode":"MOVE"}
```

| Field | Meaning |
|---|---|
| `Ts` | ISO 8601 with offset |
| `Event` | `start`, `done`, `fail`, `block` |
| `Rule` `Source` `Target` | self-explanatory |
| `Part` | the `.msmover-part` path, used for crash recovery |
| `Size` `Hash` `Mode` | bytes, verified hash, `COPY` or `MOVE` |
| `Detail` | failure reason, when there is one |

A `start` with no matching terminal record means the transfer was interrupted. On the next launch
those part files are deleted — a part file is by definition incomplete and unverified, so removing
it can never lose data.

One object per line, so it streams into any JSON-lines reader without loading the whole file.

## `msmover_index.tsv`

Optional, written at the target root when **Index file** is set. Tab-separated with a header, so
it opens directly in a spreadsheet or any TSV reader.

```text
timestamp             rule        target                                      size        hash                       source
2026-03-14 15:12:03   Thermo raw  MSTEST\MSTEST.pro\Data\MSTEST_A01_003.raw   1288490188  xxhash64:b2baad2182f1b1ab  D:\Xcalibur\Data\MSTEST_A01_003.raw
```

`target` is relative to the target folder; `source` is absolute. Appends are retried three times if
the file is locked by another machine writing at the same moment, then a warning is logged — the
index is a convenience, never a reason to fail a transfer.

## Log files

One per day, plain text, UTF-8, deleted after `LogRetentionDays`.

```text
2026-03-14 15:12:03 INFO  Thermo raw | MOVE OK  MSTEST_A01_003.raw  ->  \\storage\...  (1.2 GB, 96.4 MB/s, XxHash64:b2baad2182f1b1ab)
2026-03-14 15:12:05 WARN  Thermo raw | BADNAME.raw: Filename check: too few delimiters (found 1, expected 2). File ignored.
```

Format: `timestamp LEVEL rule | message`. The rule is `-` for application-level messages.

## Working files

| Name | Where | What |
|---|---|---|
| `<name>.msmover-part` | target | In-progress copy. Renamed to the final name only after verification. Orphans are cleaned up at startup. |
| `<name>.msmover-link` | source | A symlink being created, before it is renamed into the source's place. Should never persist. |
| `.msmover-probe-*.tmp` / `.lnktest` | target / source | Symlink pre-flight probes. Deleted immediately. |

All of these are ignored by discovery, so they can never be mistaken for data.
