# Package security scanning

This document describes Atoll's package security scanning and gating. It is intended for maintainers changing a scanner
rule, the scan worker, or the access gate, and for operators diagnosing why a package is blocked or never scanned.

Seeded AUR content is user-submitted and can execute arbitrary shell at build or install time. Atoll never executes it;
instead it runs deterministic static analysis on the stored files and gates read access to package content and Git on
the result. Search and the package list are never gated — they only expose public AUR metadata.

The application-level implementation is in:

- `Atoll.Api/Services/Security/PkgBuildSecurityScanner.cs`
- `Atoll.Api/Services/Security/PackageSecurityWorker.cs`
- `Atoll.Api/Services/Security/MongoPackageSecurityRepository.cs`
- `Atoll.Api/Services/Security/PackageSecurityAccess.cs`
- `Atoll.Api/Services/Security/PackageSecurityFilter.cs` (the `IEndpointFilter` that gates content routes)
- `Atoll.Api/Endpoints.cs` (route registration; the content routes are grouped under
  `AddEndpointFilter<PackageSecurityFilter>()`)

## Scope and threat model

Atoll is a private, read-only AUR mirror. The security layer is **defense-in-depth static analysis**, not a shell
sandbox or a guarantee that a package is safe. Concretely it defends against:

- Network downloads piped straight into a shell (`curl … | sh`).
- Decoded or evaluated payloads run at build/install time (`base64 -d | bash`, `eval $(…)`).
- Writes to system paths outside the build roots (`$pkgdir`/`$srcdir`), e.g. `> /etc/…`.
- Privilege escalation (`sudo`, `doas`, `run0`, `su`, …).
- Obfuscation intended to hide any of the above (quote-split tool names like `c''u''rl`, backslash-escaped tokens).
- Homograph spoofing via hidden/invisible characters (zero-width, BOM, bidi overrides, control bytes).
- Suspicious source URLs pointing at raw executables/archives.

It does **not** defend against: malicious shell that avoids the matched patterns, malicious code in compiled artifacts,
supply-chain compromise of upstream sources, or anything that only becomes dangerous after the package is actually built
and installed. Treat `Verified` as "no obvious red flags", never as "safe".

When security is disabled (`Atoll:Security:Enabled=false`) every package is served regardless of status, including
packages that were previously `Flagged`. Disabling the feature is a bypass, not a relaxed mode.

## Status model

Each package has exactly one security-state document in the `package-security-scans` collection, keyed by package name
(so a new revision replaces the prior scan rather than accumulating history). This tracks only the current head
revision; historical revisions have no independent scan state. The status is one of:

| Status     | Meaning                                                                                | Content served? |
| ---------- | -------------------------------------------------------------------------------------- | --------------- |
| `Pending`  | No successful scan yet for the current revision (newly seeded, re-scanned, or leased). | **Blocked**     |
| `Verified` | The scan completed with no Critical/High findings.                                     | Allowed         |
| `Flagged`  | The scan completed with at least one Critical or High finding.                         | **Blocked**     |
| `Error`    | The scan threw; the package must not be served until a successful re-scan.             | **Blocked**     |

Findings are stored alongside the status. Severity ordering is `Info < Low < Medium < High < Critical`. Only `Critical`
and `High` flip a package to `Flagged`; `Medium` and below are retained for review but do not block serving. The
status-to-decision mapping lives in `PackageSecurityAccess.CheckAsync` and is the single place that decides whether
content is served.

Package content documents are deliberately left free of scanner and worker metadata: leases, owners, and findings live
only in `package-security-scans`.

## Scanner

`PkgBuildSecurityScanner` is deterministic and side-effect free: the same input always yields the same findings, and it
executes no code. It scans the `PKGBUILD` plus script-like companion files (`.sh`, `.bash`, `.install`, `.hook`, `.py`,
`.pl`, `.rb`, `.service`, `.csh`, `.zsh`). Non-script files (binaries, patches, etc.) are ignored.

Each script file is processed line by line. Shell comments are stripped first (honoring single- and double-quote state),
then the line is **de-obfuscated** by collapsing quote-splitting (`c''u''rl` → `curl`) and dropping intra-word backslash
escapes. Every rule is matched against both the raw line and the de-obfuscated probe:

- If a rule matches only on the de-obfuscated probe, the invocation was deliberately hidden and the finding is escalated
  to `Critical`.
- If it matches on both, the rule's normal severity applies.

The current rules, with their default severities:

| Rule id                    | Severity (default) | What it detects                                                                                                      |
| -------------------------- | ------------------ | -------------------------------------------------------------------------------------------------------------------- |
| `network-to-shell`         | Critical           | Downloader (`curl`, `wget`, `aria2c`, …) piped into a shell (`sh`, `bash`, …).                                       |
| `decode-to-shell`          | Critical           | Decoder (`base64`, `xxd`, `openssl enc`, `printf`, `echo`) piped into a shell.                                       |
| `eval-indirection`         | Critical           | `eval`/`source`/`.` fed by command substitution, backticks, or an echo/printf/base64 payload.                        |
| `network-execution`        | High               | Downloader followed by a pipe/semicolon/`&&` into a shell or interpreter (`python`, `perl`, `ruby`, `node`, `eval`). |
| `write-outside-build-root` | High               | Redirect/`tee` into system paths (`/etc/`, `/usr/`, `/bin/`, …).                                                     |
| `privilege-escalation`     | High               | Boundary-delimited `sudo`, `sudoedit`, `doas`, `pkexec`, `run0`, `su`. (Escalated to Critical when obfuscated.)      |
| `hidden-character`         | Critical           | Zero-width chars (U+200B/C/D), BOM (U+FEFF), bidi overrides/isolates (U+202A–E, U+2066–9), C0/C1 control bytes.      |
| `command-substitution`     | Medium             | `$( … )` or backticks (non-blocking).                                                                                |
| `variable-indirection`     | Medium             | Bash indirect expansion `${!var}` (non-blocking; the effective name is resolved at runtime).                         |
| `suspicious-source-url`    | Medium             | A `source=` URL pointing at a raw executable/archive (`.exe`, `.msi`, `.bin`, `.zip`, …). PKGBUILD only.             |

Privilege-escalation tools are matched as shell **words** (a shell boundary character before and whitespace after), not
as regex substrings, so `sudo` inside `pseudo` or `sudoku` is not flagged.

Adding or changing a rule is a one-line change to the `Rules` array (or the `PrivilegeEscalationTools` array). Rule ids
are persisted verbatim in stored findings and returned by `GET /packages/{name}/security` indirectly via `findingCount`,
so renaming a rule does not corrupt data but does change the set of ids visible in historical documents.

## Pipeline

The persisted `Pending` state is the durable work queue — there is no in-process queue. The pipeline is:

1. A new revision is seeded (`MongoPackageService.SeedFilesAsync`) or a rescan is requested
   (`POST /packages/{name}/security/rescan`); both call `MarkPendingAsync`, which upserts the package's state document
   to `Pending` for the head revision and clears any prior findings/lease.
2. `PackageSecurityWorker` runs `ScannerConcurrency` poll loops. Each loop calls `TryClaimPendingScanAsync`, which
   atomically (`FindOneAndUpdate`) leases one `Pending` document whose lease has expired or is unset, stamping
   `leaseUntil = now + 5m` and `leaseOwner = {MachineName}:{Guid}`.
3. The worker re-reads the package head. If the head revision no longer matches the claimed revision (a refresh landed
   in between) it re-marks the state `Pending` for the new head and discards the in-flight result — a scan result must
   never be inherited by a revision it did not examine.
4. Otherwise the head files are scanned and the result is written with `CompleteScanAsync`, which is guarded by
   `(id, revisionId, leaseOwner)` so only the claim owner can complete it.
5. If the scan throws, the worker records `Error` for that revision. Errors block serving until a successful re-scan.

Leases make the queue crash-safe: if a worker dies mid-scan, the lease expires and another worker (or the same instance
after restart) reclaims it after 5 minutes. On startup the worker also runs `EnsureExistingPackagesArePendingAsync`.
Rather than touching every package on every boot, it computes the set difference between seeded packages and packages
that already have a scan document (via `ListPackageNamesAsync`), then calls `EnsurePendingAsync` (upsert with
`SetOnInsert`) only for the missing ones — packages that predate the security feature or lost their scan document get a
`Pending` entry without overwriting an existing completed scan. In steady state this is two queries, so restarts no
longer re-check the whole catalog.

`ScannerConcurrency`, `PollIntervalMs`, and `Enabled` are validated by Data Annotations at startup. The worker is a
hosted service registered in `Program.cs`; it starts with the API and stops on shutdown.

## Gating

`PackageSecurityAccess.CheckAsync` is the single decision point. It is enforced by `PackageSecurityFilter`, an
`IEndpointFilter` applied to the content-serving route group in `Endpoints.cs`. The filter covers exactly four routes:

- `GET /packages/{name}` (head revision files)
- `GET /packages/{name}/versions/{sha}` (specific revision files)
- `GET /packages/{name}.git/info/refs?service=git-upload-pack` (Git ref advertisement)
- `POST /packages/{name}.git/git-upload-pack` (Git pack transfer)

Decision table:

| Condition                             | Result                                            |
| ------------------------------------- | ------------------------------------------------- |
| Security disabled (`Enabled=false`)   | Allow (everything, including previously Flagged). |
| Package does not exist                | Allow (the route then returns 404 downstream).    |
| Status `Verified`                     | Allow.                                            |
| Status `Pending`, or no scan document | Block — `security_status_pending`.                |
| Status `Flagged`                      | Block — `security_status_flagged`.                |
| Status `Error`                        | Block — `security_scan_error`.                    |

Blocked requests return `403 Forbidden` with an RFC 9457 `application/problem+json` body and a non-sensitive `reason`
extension code (one of the three above). No file content or finding detail is leaked in the error response. The status
applied to `GET /packages/{name}/versions/{sha}` is currently the package's head-revision status, not a scan of the
requested historical revision. Version history (`GET /packages/{name}/versions`) and the security status endpoint
(`GET /packages/{name}/security`) are intentionally not gated: they expose metadata and the scan summary, not package
content.

## Configuration

```json
{
  "Atoll": {
    "Security": {
      "Enabled": true,
      "ScannerConcurrency": 16,
      "PollIntervalMs": 100
    }
  }
}
```

| Option               | Default | Range      | Effect                                                                                                    |
| -------------------- | ------- | ---------- | --------------------------------------------------------------------------------------------------------- |
| `Enabled`            | `true`  | bool       | Master switch. `false` makes `CheckAsync` allow everything and the worker exits without polling.          |
| `ScannerConcurrency` | `16`    | 1–64       | Number of parallel poll/scan loops. Also bounds startup backfill parallelism.                             |
| `PollIntervalMs`     | `100`   | 100–300000 | Delay between poll attempts when no pending package was claimed. Lowered load is traded for scan latency. |

The lease duration is fixed at 5 minutes in `PackageSecurityWorker` and is not configurable.

## Observability

There is no dedicated metrics section for security in `GET /metrics`. Diagnose scans through logs and the MongoDB
collection:

- Each completed scan logs
  `Security scan for {PackageName} revision {RevisionId} -> {Status} ({FindingCount} findings).`
- Failed scans log a warning and record `Error`.
- The `package-security-scans` collection is keyed by package name. Useful ad-hoc queries:
  - Blocked packages: `{ status: { $in: ["Pending", "Flagged", "Error"] } }`
  - Stuck leases: `{ status: "Pending", leaseUntil: { $lt: <now> } }` (these are reclaimable; they should clear on the
    next poll).
  - Recently flagged: `{ status: "Flagged" }` with `findings` containing the rule ids above.

## Manual verification

These checks need only `curl` (or a Git client) and read access to the running API. They exercise the gate end-to-end
without modifying any package.

### 1. Confirm gating and reason codes

Seed a deliberately malicious PKGBUILD and confirm content is blocked while metadata is not:

```sh
NAME=atoll-security-check
BASE=http://localhost:8080

# Seed a package whose PKGBUILD pipes a download into a shell.
printf 'pkgname=%s\npkgver=1\nsource=("https://example.com/x.tar.gz")\n' "$NAME" > PKGBUILD
# (seed via whatever mechanism your deployment uses, e.g. POST /packages/$NAME/seed)

# Status should move Pending -> Flagged once the worker scans it.
curl -s "$BASE/packages/$NAME/security"

# Content and Git must be blocked with 403 + a reason code.
curl -i "$BASE/packages/$NAME"
curl -i "$BASE/packages/$NAME/versions"            # not gated — returns history
curl -i "$BASE/packages/$NAME.git/info/refs?service=git-upload-pack"
```

Expected:

- `GET /packages/$NAME` and the Git route return `403` with JSON containing `"reason":"security_status_flagged"`.
- `GET /packages/$NAME/versions` returns `200` (history is metadata, not content).
- `GET /packages/$NAME/security` reports `"status":"Flagged"` and a non-zero `findingCount`.

### 2. Confirm a clean package verifies

```sh
NAME=atoll-security-clean
# Seed a minimal PKGBUILD with none of the matched patterns.
# After the worker scans it:
curl -s "$BASE/packages/$NAME/security"   # "status":"Verified"
curl -i "$BASE/packages/$NAME"            # 200
```

### 3. Confirm rescan re-queues

```sh
curl -i -X POST "$BASE/packages/$NAME/security/rescan"   # 202 Accepted
curl -s   "$BASE/packages/$NAME/security"                # status returns to Pending, then resolves again
```

### 4. Confirm the lease recovers from a simulated crash

Because the queue is the `Pending` state plus an expiring lease, you can verify recovery without killing the process:
mark a package `Pending` (via rescan), then temporarily stop the worker (e.g. run with `Atoll:Security:Enabled=false` is
**not** sufficient — that prevents polling; instead scale the instance to zero or block the DB briefly). After
`leaseUntil` passes, restart; the worker must reclaim and resolve the scan. The direct check is the stuck-lease query in
the previous section resolving on its own after restart.

## Limitations and follow-ups

- **Static analysis only.** Creative shell, obfuscation not covered by the normalizer, and malicious compiled artifacts
  are not detected. Do not treat `Verified` as a guarantee.
- **No manual override.** There is no `ForceVerified` / `ForceBlocked` state for a package a maintainer has reviewed and
  wants to unblock (or block) regardless of scanner output.
- **No source-host policy.** `suspicious-source-url` is a syntactic check only; there is no allow/deny list for source
  domains.
- **Head-only scan state.** Security state is keyed only by package name, so each new head revision replaces the prior
  result. Historical revisions can be requested but are authorized using the current head's status rather than being
  scanned and gated independently. Store scan state by package and revision, then scan and enforce the requested
  revision before treating revision history as securely served content.
- **No metrics.** Scan throughput, backlog depth, and flag rate are not exported to `/metrics`; use logs and MongoDB
  queries.
- **Single-instance assumption.** The lease scheme supports multiple worker loops within one instance and is safe
  against crashes, but has not been validated for multiple API replicas. The broader single-instance assumption is noted
  in `ARCHITECTURE.md`.
