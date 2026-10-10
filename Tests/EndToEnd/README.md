# Cross-repository diagnostic proof

Run `run.sh QUASAR_CHECKOUT BACKEND_CHECKOUT PLUGIN_SDK_DIRECTORY PRIVATE_ARTIFACTS_DIRECTORY [SERVER_LIST_CHECKOUT]` with .NET 10 and the matching Magnetar SDK. It builds the package and fixture, starts two actual Host processes plus a disposable backend and optional website bound to loopback, invokes real plugin capture in separate child processes, and runs the actual Quasar uploader. It never starts Quasar's web service, contacts live GitHub/model APIs, or changes a game server.

The checks cover:

- Two enrolled Host identities, different random credentials and routes; cross-Host credentials are rejected.
- Distinct Host/node/run/transfer correlation through encrypted backoffice validation.
- Ordinary plugin `Info` output is flushed into `log_batch` reports on both Hosts and reaches encrypted storage with its category intact.
- Automatic machine enrollment is consent-gated and requires no manually configured installation ID, upload token or website verifier secret.
- Quiet Quasar delivery through its real logger/collector/uplink, naturally batched without crashes or a synthetic heartbeat; encrypted evidence grants a fresh backend attestation.
- With the optional server-list checkout: the actual website accepts signed backend pushes, remains hidden without separate listing opt-in, publishes and joins after opt-in, withdraws independently of diagnostics consent, expires leases during backend outage and recovers after restart.
- Backend outage while both Hosts collect, durable Quasar handover, source acknowledgment, restart and retry.
- Independent Host lease expiry through its authenticated policy endpoint.
- Revocation while Host B is offline; restart/reconnect applies the current generation, purges its sharing queue and never uploads the revoked archive.

On Linux it sends SIGSEGV only to its own `/bin/sleep` fixture and attempts exact-process systemd core collection. The output states explicitly whether this account can read an OS core. All Host/backend processes stop on exit. Fixture credentials are random, supplied only through child environments, and never printed; the private artifact directory is owner-only and contains disposable keys and evidence. Keep it out of source control. No global OS crash policy changes are made.

The harness uses Quasar's existing internal test seams via its friend assembly name; checkout paths are explicit MSBuild properties. Its loopback routing adapter verifies every enrolled synthetic Host address/port before routing, rather than sending all requests to one process.

During coordinated builds, set `DIAGNOSTICS_SKIP_PACKAGE_BUILD=1` only after the shared package has already been rebuilt into the local feed. Use a fresh private artifact/cache directory when replacing an unpublished local 0.1.0 package. The script prints `END-TO-END BUILD COMPLETE` before starting its isolated runtime fixtures, so other repository builds can safely resume.
