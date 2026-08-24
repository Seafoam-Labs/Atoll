# Package security scanning

This document describes Atoll's package security scanning and gating. It is intended for maintainers changing a scanner
rule, the scan worker, or the access gate, and for operators diagnosing why a package is blocked or never scanned.

Seeded AUR content is user-submitted and can execute arbitrary shell at build or install time. Atoll never executes it;
instead it runs deterministic static analysis on the stored files and gates read access to package content and Git on
the result. Search and the package list are never gated — they only expose public AUR metadata.

The application-level implementation is in:

- `Atoll.Api/Services/Security/PkgBuildSecurityScanner.cs` — thin facade: iterates files, delegates to the
  components below, and reduces their findings into the final `ScanResult`.
- `Atoll.Api/Services/Security/Scanning/ShellContentScanner.cs` — owns the rule set and the risky/privilege tool lists,
  and runs the per-line scan loop (rule matching, tool detection, obfuscation escalation), quote-aware match
  suppression, and heredoc body tracking.
- `Atoll.Api/Services/Security/Scanning/ShellSyntax.cs` — shell-aware primitives shared across rules: comment
  stripping, de-obfuscation normalization with a source map back to the original line, quote-region tracking,
  tool-boundary matching, hidden-codepoint detection.
- `Atoll.Api/Services/Security/Scanning/PkgBuildSourceUrlScanner.cs` — inspects `source=` declarations for suspicious
  archive/executable URLs (PKGBUILD only).
- `Atoll.Api/Services/Security/Scanning/HomographScanner.cs` — detects homograph spoofing in PKGBUILD metadata fields
  (`pkgname`, `depends`, `makedepends`, `url`, `source`): hidden/invisible characters including combining marks,
  Latin mixed with Cyrillic/Greek/Armenian, fullwidth ASCII lookalikes, and confusable-skeleton folding
  (PKGBUILD only).
- `Atoll.Api/Services/Security/Scanning/PackageBuildFileClassifier.cs` — decides which files are scannable
  (`PKGBUILD` plus script-like companion extensions).
- `Atoll.Api/Services/Security/Scanning/LocalSourceBinaryScanner.cs` — classifies source files that are ELF
  executables or contain binary bytes: blocking `Critical` for ELF/unrecognized binaries, non-blocking `Medium` for
  inert media, certificate/signature files, and unencodable text (applies to every file, not just script-like
  extensions).
- `Atoll.Api/Services/Security/PackageSecurityWorker.cs`
- `Atoll.Api/Services/Security/MongoPackageSecurityRepository.cs`
- `Atoll.Api/Services/Security/PackageSecurityAccess.cs`
- `Atoll.Api/Services/Security/PackageSecurityFilter.cs` (the `IEndpointFilter` that gates content routes)
- `Atoll.Api/Endpoints.cs` (route registration; the content routes are grouped under
  `AddEndpointFilter<PackageSecurityFilter>()`)

The `Scanning/` types are `internal static` and are covered by focused unit tests under
`Atoll.Api.Tests/Security/Scanning/` (`ShellSyntaxTests`, `ShellContentScannerTests`, `HomographScannerTests`,
`PkgBuildSourceUrlScannerTests`, `LocalSourceBinaryScannerTests`, `PackageBuildFileClassifierTests`). The facade
itself is covered end-to-end by `Atoll.Api.Tests/Security/PkgBuildSecurityScannerTests`, which doubles as a regression
fixture (it pins the behavior of a real-world `shelly` PKGBUILD).

## Scope and threat model

Atoll is a private, read-only AUR mirror. The security layer is **defense-in-depth static analysis**, not a shell
sandbox or a guarantee that a package is safe. Concretely it defends against:

- Network downloads piped straight into a shell (`curl … | sh`).
- Decoded or evaluated payloads run at build/install time (`base64 -d | bash`, `eval $(…)`).
- Writes to system paths outside the build roots (`$pkgdir`/`$srcdir`), e.g. `> /etc/…`.
- Privilege escalation (`sudo`, `doas`, `run0`, `su`, …).
- Obfuscation intended to hide any of the above (quote-split tool names like `c''u''rl` and `c'u'rl`,
  backslash-escaped tokens).
- Homograph spoofing: hidden/invisible characters (zero-width, BOM, bidi overrides, control bytes) anywhere in script
  content, and field-level checks on PKGBUILD metadata (`pkgname`, `depends`, `makedepends`, `url`, `source`) against
  lookalike download hosts and typosquatted dependency names — invisible/combining characters, Latin mixed with
  Cyrillic/Greek/Armenian, fullwidth ASCII, and ASCII-foldable confusables.
- Suspicious source URLs pointing at raw executables/archives.
- Local source files shipped as ELF executables or binary blobs, which cannot be reviewed as text.

It does **not** defend against: malicious shell that avoids the matched patterns, malicious code hidden inside a binary
(even though the binary itself is now flagged on presence), supply-chain compromise of upstream sources, or anything
that only becomes dangerous after the package is actually built and installed. Treat `Verified` as "no obvious red
flags", never as "safe".

When security is disabled (`Atoll:Security:Enabled=false`) every package is served regardless of status, including
packages that were previously `Flagged`. Disabling the feature is a bypass, not a relaxed mode.

## Status model

Each retained package revision has its own security-state document in the `package-security-scans` collection, keyed by
the composite id `{packageName}:{revisionId}` (revision ids are content-addressed SHA-256 hashes, so the same content
always maps to the same id). A new head revision gets a fresh `Pending` document; previous revisions keep their own
scan state, so a flagged revision blocks only itself. Each document also carries a denormalized `isHead` flag so the
gate can resolve the head scan without a second read against the `packages` collection. The status is one of:

| Status | Meaning | Content served? |
| --- | --- | --- |
| `Pending` | No successful scan yet for that revision (newly seeded, re-scanned, or leased). | **Blocked** |
| `Verified` | The scan completed with no Critical/High findings. | Allowed |
| `Flagged` | The scan completed with at least one Critical or High finding. | **Blocked** |
| `Error` | The scan threw; the revision must not be served until a successful re-scan. | **Blocked** |

Findings are stored alongside the status. Severity ordering is `Info < Low < Medium < High < Critical`. Only `Critical`
and `High` flip a revision to `Flagged`; `Medium` and below are retained for review but do not block serving. The
status-to-decision mapping lives in `PackageSecurityAccess.CheckAsync` and is the single place that decides whether
content is served.

Package content documents are deliberately left free of scanner and worker metadata: leases, owners, and findings live
only in `package-security-scans`.

## Scanner

`PkgBuildSecurityScanner` is deterministic and side-effect free: the same input always yields the same findings, and it
executes no code. Every file is first checked for binary content — ELF magic, NUL/control bytes, or undecodable UTF-8 —
because a binary source file cannot be reviewed as text and may hide malicious code. The severity depends on what the
content looks like: ELF executables and unrecognized binaries stay `Critical` (blocking), while content that cannot
execute on its own — recognized media/data magic bytes, certificate/signature files, and text whose only binary
indicator is undecodable bytes — is emitted as a non-blocking `Medium` finding (see the `local-binary` notes below).
Script-like files (the `PKGBUILD` plus companions `.sh`, `.bash`, `.install`, `.hook`, `.py`, `.pl`, `.rb`, `.service`,
`.csh`, `.zsh`) are then scanned line by line for shell threats. Remaining non-script, non-binary files (patches, etc.)
are ignored.

Each script file is processed line by line. Shell comments are stripped first (honoring single- and double-quote state),
then the line is **de-obfuscated** by collapsing quote-splitting (`c''u''rl` → `curl`), stripping quotes that sit
between two word characters (`c'u'rl` → `curl` — the shell's quote removal makes these part of the word), and dropping
intra-word backslash escapes. Quotes at word edges are kept: `'npm'` stays a quoted string (display/argument text),
not an invocation. Every rule is matched against both the raw line and the de-obfuscated probe:

- If a rule matches only on the de-obfuscated probe, the invocation was deliberately hidden and the finding is escalated
  to `Critical`.
- If it matches on both, the rule's normal severity applies.

Matching is quote-aware: the scanner tracks the shell quote region at every position of the line and drops matches
that cannot execute.

- `$(…)`, backticks and `${!…}` only expand outside single quotes, so matches inside single quotes — or behind a
  backslash escape (`\$(…)`) — are dropped. Double-quoted expansions still execute and stay flagged.
- Redirects and `tee` targets are dropped when the operator sits inside any quoted string or is backslash-escaped —
  `echo " >> /etc/…"` is literal text, not a write.
- Obfuscation escalation is gated on the same analysis: a match that only appears on the de-obfuscated probe and maps
  entirely inside quoted regions of the raw line is an escape-stripping artifact (e.g. echo'd instructions containing
  `\$(sudo …)`, where the backslash prevents execution) rather than hidden intent — it is dropped or kept at the
  rule's normal severity instead of escalating to `Critical`. Genuine quote-split obfuscation outside quotes
  (`s''u''d''o`) still escalates.

Heredoc bodies are tracked across lines. A quoted delimiter (`<<'EOF'`, `<<"EOF"`, `<<\EOF`) makes the body literal
data, so the non-blocking expansion rules (`command-substitution`, `variable-indirection`) are suppressed inside it.
As a conservative guard, suppression is lifted when the declaration pipes the body into a shell or interpreter
(`cat <<'EOF' | sh`), and blocking rules always stay active inside bodies. Unquoted-delimiter bodies expand and are
scanned as ordinary lines.

The current rules, with their default severities:

| Rule id | Severity (default) | What it detects |
| --- | --- | --- |
| `network-to-shell` | Critical | Downloader (`curl`, `wget`, `aria2c`, …) piped into a shell (`sh`, `bash`, …). |
| `decode-to-shell` | Critical | Decoder (`base64`, `xxd`, `openssl enc`, `printf`, `echo`) piped into a shell. |
| `eval-indirection` | Critical | `eval`/`source`/`.` fed by command substitution, backticks, or an echo/printf/base64 payload. |
| `network-execution` | High | Downloader followed by a pipe/semicolon/`&&` into a shell or interpreter (`python`, `perl`, `ruby`, `node`, `eval`). |
| `write-outside-build-root` | High | Redirect/`tee` into system paths (`/etc/`, `/usr/`, `/bin/`, …). |
| `privilege-escalation` | High | Boundary-delimited `sudo`, `sudoedit`, `doas`, `pkexec`, `run0`, `su`. (Escalated to Critical when obfuscated.) |
| `hidden-character` | Critical | Zero-width chars (U+200B/C/D), BOM (U+FEFF), bidi overrides/isolates (U+202A–E, U+2066–9), C0/C1 control bytes. |
| `homograph` | High | Spoofing in PKGBUILD metadata values (`pkgname`, `depends`, `makedepends`, `url`, `source`): invisible/combining characters, Latin mixed with Cyrillic/Greek/Armenian, fullwidth ASCII lookalikes (U+FF01–FF5E), and non-ASCII that folds to an ASCII skeleton. PKGBUILD only. |
| `command-substitution` | Medium | `$( … )` or backticks (non-blocking). |
| `variable-indirection` | Medium | Bash indirect expansion `${!var}` (non-blocking; the effective name is resolved at runtime). |
| `suspicious-source-url` | Medium | A `source=` URL pointing at a raw executable/archive (`.exe`, `.msi`, `.bin`, `.zip`, …). PKGBUILD only. |
| `local-binary` | Critical (Medium for inert content) | A source file that is an ELF executable or contains binary bytes (NUL, control, undecodable UTF-8). Whole-file check; severity split by content, see below. |

Privilege-escalation tools are matched as shell **words** (a shell boundary character before and whitespace after), not
as regex substrings, so `sudo` inside `pseudo` or `sudoku` is not flagged.

`local-binary` is the one rule that is not a per-line shell check: it runs once per file on the whole content and
applies to every file in the package regardless of extension. Its severity is split by content: ELF executables and
unrecognized binaries are `Critical` and block the package, while content that cannot execute on its own is retained
as non-blocking `Medium`:

- Inert media recognized by magic bytes on the (UTF-8-decoded) content — PNG, JPEG, GIF, BMP, ICO, WebP,
  TrueType/OpenType/WOFF/WOFF2 fonts, PDF. Content-based, so renaming `.exe` to `.png` does not help. Archives are
  deliberately not allowlisted: they can carry executables.
- Certificate/signature files by extension (`.sig`, `.asc`, `.gpg`, `.cer`, `.crt`, `.pem`) — inert data with no
  reliable magic bytes. ELF content still takes precedence and stays `Critical`.
- Text whose only binary indicator is undecodable UTF-8 (U+FFFD from legacy encodings), with no NUL or control
  characters.

`homograph` is the other non-shell rule: it runs only on the PKGBUILD and inspects the extracted values of the
`pkgname`, `depends`, `makedepends`, `url`, and `source` assignments (including indented ones inside `package()`
functions). Comments are stripped and quotes removed before checking, so non-ASCII prose after `#` never fires it.
Four checks run in order and the first hit wins per line: hidden/invisible characters (the zero-width/bidi/control
set plus format and combining marks — a mark like U+0670 prepended to a URL scheme is invisible yet changes the
value — checked on the NFC-normalized value so decomposed accents are not mistaken for hidden marks), Latin mixed
with Cyrillic/Greek/Armenian (other scripts such as CJK and Hangul are ignored: they cannot spoof ASCII and are
legitimate in internationalized names), fullwidth ASCII lookalikes, and confusable-skeleton folding (a ~45-entry
Cyrillic/Greek table without accented Latin letters). Free prose (`pkgdesc`, comments) is deliberately out of scope.

The remaining rules are shell-line rules and only run on scannable script files.

Adding or changing a shell rule is a one-line change to the `Rules` array in `ShellContentScanner` (or the
`PrivilegeEscalationTools` / `RiskyTools` arrays in the same file). The `local-binary` and `homograph` rules remain
separate because they are whole-file and field-value checks respectively, not shell-line rules. Rule ids are persisted
verbatim in stored findings and exposed indirectly by `GET /packages/{name}/security` through `findingCount`, so
renaming a rule does not corrupt data but changes the ids visible in historical documents.

## Pipeline

The persisted `Pending` state is the durable work queue — there is no in-process queue. The pipeline is:

1. A new revision is seeded (`MongoPackageService.SeedFilesAsync`) or a rescan is requested
   (`POST /packages/{name}/security/rescan`, optionally with `?revision={sha}`); both call `MarkPendingAsync`, which
   upserts the `(package, revision)` state document to `Pending` and clears any prior findings/lease for that revision.
2. `PackageSecurityWorker` runs `ScannerConcurrency` poll loops. Each loop calls `TryClaimPendingScanAsync`, which
   atomically (`FindOneAndUpdate`) leases one `Pending` document whose lease has expired or is unset, stamping
   `leaseUntil = now + 5m` and `leaseOwner = {MachineName}:{Guid}`.
3. The worker re-reads the claimed revision via `GetRevisionAsync`. If the revision is no longer retained in the
   package's history (it aged out of `MaxRevisions`, or the package was deleted) the claim is deleted — a scan result
   must never be written for content that can no longer be served.
4. Otherwise the claimed revision's files are scanned and the result is written with `CompleteScanAsync`, which is
   guarded by `(id, leaseOwner)` so only the claim owner can complete it. Because the claim is keyed by revision, a
   refresh that swaps the head in between does not disturb the in-flight scan: the result is tied to the exact
   revision that was examined.
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

`PackageSecurityAccess.CheckAsync` is the single decision point. `PackageSecurityFilter`, applied to the
content-serving route group in `Endpoints.cs`, enforces it for head content, a requested revision, and both Git Smart
HTTP routes. Decision table:

| Condition | Result |
| --- | --- |
| Security disabled (`Enabled=false`) | Allow (everything, including previously Flagged). |
| Package does not exist | Allow (the route then returns 404 downstream). |
| Status `Verified` | Allow. |
| Status `Pending`, or no scan document | Block — `security_status_pending`. |
| Status `Flagged` | Block — `security_status_flagged`. |
| Status `Error` | Block — `security_scan_error`. |

Blocked requests return `403 Forbidden` with an RFC 9457 `application/problem+json` body and a non-sensitive `reason`
extension code (one of the three above). No file content or finding detail is leaked. The requested-version route is
gated on that revision's status; the head-content and Git routes use the head status. Version history and the security
status endpoint remain ungated because they expose only metadata and scan summaries.

**UI exception:** the Blazor Files tab (`PackageDetailsService.GetFilesAsync`) is deliberately *not* gated — it
serves the file tree and contents of flagged revisions read-only, with a warning banner, so users can inspect the
content that triggered the findings. Only the API/Git surfaces go through `PackageSecurityFilter`.

> **Git materialization is scan-status aware:** when security is enabled, the bare repository is materialized from
> `Verified` revisions only. A `Flagged`, `Pending`, or `Error` historical revision is excluded from the cloneable
> Git history, so it cannot be reached via `git clone` followed by `git checkout <sha>` (the equivalent
> `GET /packages/{name}/versions/{sha}` request is also blocked). The `.atoll-head` marker embeds every retained
> revision id and its scan status, so any status change, history change, or toggling of security invalidates the
> marker and triggers a lazy rebuild on the next Git request. The `/versions` endpoint remains the full-history,
> metadata-only surface regardless of scan status.

## Configuration

```json
{
  "Atoll": {
    "Security": {
      "Enabled": true,
      "ScannerConcurrency": 4,
      "PollIntervalMs": 100
    }
  }
}
```

| Option | Default | Range | Effect |
| --- | --- | --- | --- |
| `Enabled` | `true` | bool | Master switch. `false` makes `CheckAsync` allow everything and the worker exits without polling. |
| `ScannerConcurrency` | `4` | 1–64 | Number of parallel poll/scan loops. Also bounds startup backfill parallelism. |
| `PollIntervalMs` | `100` | 100–300000 | Delay between poll attempts when no pending package was claimed. Lowered load is traded for scan latency. |

The lease duration is fixed at 5 minutes in `PackageSecurityWorker` and is not configurable. The
`atoll_securityscan_pending` gauge is refreshed every 30 seconds, independent of `ScannerConcurrency`.

## Observability

`GET /metrics` serves Prometheus-format OpenTelemetry metrics. The `atoll_securityscan_*` instruments are backed by
`SecurityScanStatusStore`, updated by `PackageSecurityWorker` as scans finish:

| Metric | Meaning |
| --- | --- |
| `atoll_securityscan_completed_total` | Scans that reached a terminal status (verified + flagged + errored). |
| `atoll_securityscan_verified_total` | Scans that completed `Verified`. |
| `atoll_securityscan_flagged_total` | Scans that completed `Flagged`. |
| `atoll_securityscan_errored_total` | Scans that failed and were marked `Error`. |
| `atoll_securityscan_dropped_total` | Claims dropped because the claimed revision aged out of the retained history before it could be scanned. |
| `atoll_securityscan_pending` | Backlog depth: the number of `Pending` scan documents. Refreshed every 30 seconds. |
| `atoll_securityscan_last_finished_timestamp` | Unix time of when the last scan completed or errored. |

Content is not served until the head revision is scanned, so compare `atoll_securityscan_pending` against the
bulk-seed and package-refresh throughput counters on the same endpoint to see whether the scanner keeps up with
ingestion.

Diagnose individual scans through logs and the MongoDB collection:

- Each completed scan logs
  `Security scan for {PackageName} revision {RevisionId} -> {Status} ({FindingCount} findings).`
- Failed scans log a warning and record `Error`.
- The `package-security-scans` collection is keyed by `{packageName}:{revisionId}`. Useful ad-hoc queries:
  - Blocked revisions: `{ status: { $in: ["Pending", "Flagged", "Error"] } }`
  - All scan state for one package: `{ packageName: "<name>" }`
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
curl -i -X POST "$BASE/packages/$NAME/security/rescan"                    # 202 Accepted (head revision)
curl -i -X POST "$BASE/packages/$NAME/security/rescan?revision=<sha>"     # 202 Accepted (specific revision)
curl -s   "$BASE/packages/$NAME/security"                                 # revision returns to Pending, then resolves again
```

### 4. Confirm the lease recovers from a simulated crash

Because the queue is the `Pending` state plus an expiring lease, you can verify recovery without killing the process:
mark a package `Pending` (via rescan), then temporarily stop the worker (e.g. run with `Atoll:Security:Enabled=false` is
**not** sufficient — that prevents polling; instead scale the instance to zero or block the DB briefly). After
`leaseUntil` passes, restart; the worker must reclaim and resolve the scan. The direct check is the stuck-lease query in
the previous section resolving on its own after restart.

## Limitations and follow-ups

- **Static analysis only.** Creative shell and obfuscation not covered by the normalizer are not detected, and while
  binary/ELF source files are flagged on presence, their contents cannot be inspected for malicious behavior. Do not
  treat `Verified` as a guarantee.
- **Binary detection runs on UTF-8-decoded strings.** Package content reaches the scanner already decoded from UTF-8,
  so invalid byte sequences collapse to the replacement character (U+FFFD) instead of being inspected as raw bytes. This
  is enough to catch ELF/NUL/control-byte content, but a legitimate text file containing a literal U+FFFD is retained
  as a non-blocking `Medium` finding (unencodable text). The inert-media magic matching works within the same
  constraint — signatures are anchored on the bytes that survive decoding, which is precise for the allowlisted formats
  but weaker than raw-byte matching. Byte-exact detection would require the seed paths (`MongoPackageService`,
  `AurMirror`) to surface raw bytes.
- **Heredoc prose can still block.** Quoted-delimiter heredoc bodies suppress only the non-blocking expansion rules;
  `sudo`/redirect-looking prose inside them can still yield blocking `High` findings. Extending suppression to
  blocking rules requires handling pipe-to-installer patterns (`cat <<EOF | sh`, `install < /dev/stdin`) safely first.
- **Homograph checks are field-scoped.** Only the extracted values of single-line `pkgname`/`depends`/`makedepends`/
  `url`/`source` assignments are checked; arrays spanning multiple lines, other fields, and free prose are out of
  scope. Legitimate internationalized names can still be flagged: Greek is an ASCII-lookalike-prone script, so a
  Greek IDN (e.g. `π.duncano.de`) trips the mixed-script check — accepted knowingly, it is rare in the corpus.
  Conversely, single-script spoofing that never mixes Latin and does not fold to ASCII (e.g. pure Cyrillic using
  letters outside the confusables table) is not detected.
- **No manual override.** There is no `ForceVerified` / `ForceBlocked` state for a package a maintainer has reviewed and
  wants to unblock (or block) regardless of scanner output.
- **No source-host policy.** `suspicious-source-url` is a syntactic check only; there is no allow/deny list for source
  domains.
- **Git history is the verified subset only.** When security is enabled, only `Verified` revisions are materialized
  into the bare repository, so a `Flagged` historical revision is not serveable over Git (`git clone` + `git checkout
  <sha>` fails for it). The `.atoll-head` marker embeds retained revision ids and scan statuses; any status change or
  history change invalidates it and triggers a lazy rebuild. `GET /packages/{name}/versions/{sha}` returns the same
  verdict. The `/versions` endpoint still lists the full history (metadata only). A Verified -> Flagged flip makes the
  old commit unreachable (dangling object on disk); unreachable objects are never advertised or fetchable, but a
  periodic `git gc --prune` in the repositories directory reclaims the disk space.
- **Single-instance assumption.** The lease scheme supports multiple worker loops within one instance and is safe
  against crashes, but has not been validated for multiple API replicas. The broader single-instance assumption is noted
  in `ARCHITECTURE.md`.

## Alignment with shelly-alpm

Atoll's scanner shares lineage with the security validators of
[shelly](https://github.com/Seafoam-Labs/Shelly-ALPM) (Zig Arch package manager): the risky/privilege tool lists were
originally identical. The two enforce differently — shelly advises a human who approves the build, Atoll
auto-blocks serving on High/Critical — so shelly's noisier rules must not be ported wholesale. This mapping is
the reference for a future "shelly changed, catch up" task.

**Provenance:** last full comparison against shelly commit `8988d056` (2026-08-21), executed 2026-08-23
(analysis and per-phase corpus measurements in the working notes that produced it). Re-run the comparison when
shelly's validators change meaningfully; the relevant files are `post_install_validator.zig`,
`homograph_validator.zig`, `local_source_validator.zig`, and `parser/shell_scan.zig` (under
`Shelly.PackageManager/src/pkgbuild/`). The tool lists are the likeliest drift point — they are plain arrays in
`ShellContentScanner.cs` and trivially diff-able against shelly's.

| Shelly validator/concept | Atoll counterpart | Divergence (intentional) |
| --- | --- | --- |
| `post_install_validator.zig` risky tools | `risky-tool` (Medium) | Atoll adds a quoted-region exemption (shelly's tests document quoted-string FPs it accepts as advisory) |
| `post_install_validator.zig` privilege tools | `privilege-escalation` (High, Critical when obfuscated) | Same; plus quote exemption and obfuscation escalation |
| Bare `eval` token → critical | `eval-indirection` (Critical) | Atoll requires a dynamic operand — avoids `grep eval` FPs |
| Decode-to-shell | `decode-to-shell` (Critical) | Atoll superset (`openssl enc`, more shell targets) |
| — | `network-to-shell` / `network-execution` | Atoll-only, shelly covers these only indirectly |
| Command substitution / variable indirection (naive) | `command-substitution` / `variable-indirection` (Medium) | Atoll is quote-aware and heredoc-aware; shelly matches naive substrings |
| — | `write-outside-build-root` (High) | Atoll-only |
| `homograph_validator.zig` | `homograph` (High, `HomographScanner`) | Ported conceptually: same four checks, field-scoped to PKGBUILD metadata; CJK/Hangul excluded from the mixed-script check, no accented Latin in the confusables table (corpus-driven precision choices) |
| `local_source_validator.zig` (ELF, first 64 bytes of `source=` files) | `local-binary` (Critical/Medium, `LocalSourceBinaryScanner`) | Atoll checks every file, whole content, with a magic-byte severity split for inert media |
| Obfuscation normalization (edge + intra-word quotes) | `NormalizeForMatching` (intra-word quotes only) | Edge-quote stripping would re-introduce quoted-string FPs in Atoll's blocking model |
| `shell_scan.zig` segmentation (`split_shell_segments`, heredocs) | `ShellSyntax` quoted masks + heredoc tracking in `ShellContentScanner` | Adopted for FP suppression; shelly's validators themselves don't suppress on it |
| `suspicious-source-url`-style URL validation | `suspicious-source-url` (Medium) | Atoll's host-only extension matching is deliberate and test-pinned |
| Install-script scope labels | — | Not adopted (nice-to-have) |
| Review digest/TOCTOU, sandbox/Landlock | — | Not applicable: Atoll never executes or live-reviews content |

## Transport security & response compression

Atoll enables in-app response compression (Brotli and Gzip) for dynamic HTML and API responses. When configuring
response compression, keep the following security considerations in mind:

- **HTTPS compression risks (CRIME / BREACH attacks):** Compressing dynamically generated responses over TLS can
  introduce side-channel vulnerabilities such as CRIME and BREACH. If an attacker can inject chosen plaintext into an
  HTTPS request and measure the exact encrypted response size, they may deduce secrets (such as session tokens or CSRF
  tokens) reflected in the response body.
- **Default configuration:** ASP.NET Core's response compression middleware deliberately disables compression for HTTPS
  requests by default (`EnableForHttps = false`). In Atoll's standard deployment (ECS behind an ALB or reverse proxy),
  the connection between the proxy and Kestrel is plain HTTP, so compression applies automatically without enabling
  `EnableForHttps`.
- **Mitigation and TLS termination:** If TLS is ever terminated directly inside Kestrel and `EnableForHttps` is set to
  `true`, ensure all pages containing sensitive per-user secrets or tokens implement BREACH mitigations (such as
  randomized padding or antiforgery token masking). Because Atoll serves public package catalog metadata and uses
  ASP.NET Core antiforgery tokens, the residual risk in current private/internal mirror setups is low.
