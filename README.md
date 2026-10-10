# Error Reporting

Space Engineers plugin for the game client (Pulsar) and the server (Magnetar).
Reports crashes and critical errors to central logging for analysis.

The server plugin is a required Magnetar core plugin. It subscribes before game startup to
`PluginSdk.Logging.Logger.EntryEmitted` and the process unhandled-exception event. It writes
bounded, atomic JSON incidents locally; it never uploads, encrypts, generates dumps, or changes
runtime exception handling. Local capture works without Quasar. The client remains a template.

`ERROR_REPORTING_DIRECTORY` selects the local incident directory. Without it, the plugin uses
`Diagnostics` below the launcher directory. `ERROR_REPORTING_CONTEXT` is a JSON object containing
installation, Host, run, deployment, cluster, node and role strings supplied by the launcher.
A standalone run receives a fresh run ID. Files use schema version 1 and camelCase fields:
`schemaVersion`, `reportId` (GUID N), `capturedAtUtc`, `source`, `kind`, `severity`, `message`,
`exception`, and `context`. Error/Critical records and fatal exceptions are captured independently of upload consent. Ordinary SDK logs are collected into separate batches only while a valid diagnostic consent lease is present.
Recognized structured correlation fields include transfer ID, partition, phase and reason code;
arbitrary structured payloads and world state are excluded.

The spool permits 32 incidents per minute and 256 pending files. Fatal exceptions bypass the
rate limit and replace the oldest pending file if needed. Message/exception lengths are capped
at 8/32 KiB. Concurrent writers may drop incidents instead of blocking a game thread; fatal
capture waits at most 100 ms for the writer lock. Local I/O failures are isolated. Quasar.Host
collects these files, and Quasar separately controls external sharing and dump consent.

Build against a Magnetar PluginSdk exposing `Logger.EntryEmitted`. The capture self-check runs with:

```sh
dotnet run --project Tests/Capture/Capture.csproj -p:MagnetarBinDir=/path/to/sdk/
```

The directory must contain `PluginSdk.dll`. The check covers correlation, exception evidence,
severity filtering, exclusion of world-state payloads, rate limiting, the fatal reserve, durable log batching, consent changes, lease expiry and recovery after batch publication.

## Transport and integration

`Diagnostics/` contains the reusable .NET 10 `CometWorks.Diagnostics` archive/protocol package. It seals authenticated AES-256 ZIPs and wraps each archive's random password with the backoffice RSA public key. Quasar.Host packages local evidence; Quasar alone uploads it. The backend holds the private keys and restricts human access through CometWorks GitHub OAuth.

See [the end-to-end harness](Tests/EndToEnd/README.md) for the repeatable cross-repository proof. Build the matching PluginSdk and archive package before consumers. Activate the hub's draft ErrorReporting manifest only after this source and the required SDK are released; a template-only or invented commit must never be pinned.

## Ordinary log batches

`ERROR_REPORTING_POLICY_FILE` points to the current Quasar-issued consent lease. Without a readable, unexpired diagnostics grant, ordinary logging produces no batch. Each batch belongs to one consent generation; withdrawal, expiry or a later opt-in discards an old pending journal. Existing log files are never scanned retrospectively to fill a batch.

The source-compatible `ServerPlugin/LogBatchCapture.cs` helper is compiled both into the plugin and into `CometWorks.Diagnostics`. It accumulates timestamped source/severity/message lines in a durable, atomically replaced pending journal. The completed incident has `kind: "log_batch"`, an NDJSON `message`, and context fields `captureConsentGeneration`, `batchStartedAtUtc`, `batchEndedAtUtc` and `lineCount`. Completion retains a stable report ID across retry after an interrupted publication.

Batches close after 30 seconds, 64 lines or 7,680 message characters; individual log messages truncate at 512 characters. Process shutdown explicitly flushes an eligible partial batch. The collector can poll more frequently without creating tiny reports. A separate quota of 32 completed `log-<id>.json` files evicts old ordinary batches without consuming the 256-file error/fatal quota or touching `run.json`. Cross-process writers use a nonblocking file lock; overload or storage failure can drop ordinary lines. Collection cannot recurse into SDK logging.

Quasar collects stdout through the same helper, and its own logging provider batches ordinary application messages. The collector rechecks batch consent generation before encryption and reserves archive capacity for incidents. The backend stores ordinary log archives but excludes `log_batch` reports from automated analysis and issue creation.
