# Package security scanning

This document describes Atoll's package security scanning and gating. It is intended for maintainers changing a
scanner rule, the scan worker, or the access gate, and for operators diagnosing why a package is blocked or never
scanned.

Seeded AUR content is user-submitted and can execute arbitrary shell at build or install time. Atoll never executes it;
it runs deterministic static analysis on the stored files and gates read access to package content and Git on the
result. Search, package lists, and version history are never gated — they only expose public AUR metadata.

The implementation is in:

- `Atoll.Api/Services/Security/PkgBuildSecurityScanner.cs` — facade: iterates files, delegates to the components
  below, reduces their findings into the final `ScanResult`.
- `Atoll.Api/Services/Security/Scanning/ShellContentScanner.cs` — owns the rule set and the risky/privilege tool
  lists; runs the per-line scan loop (rule matching, tool detection, obfuscation escalation) and coordinates the
  collaborators below.
- `Atoll.Api/Services/Security/Scanning/ShellSyntax.cs` — shell-aware primitives shared across rules: comment
  stripping, de-obfuscation normalization with a source map, quote-region tracking, tool-boundary matching.
- `Atoll.Api/Services/Security/Scanning/ShellHeredocs.cs` — heredoc declaration parsing and cross-line body
  tracking (quoted-delimiter suppression, pipes into interpreters).
- `Atoll.Api/Services/Security/Scanning/ShellEvalClassifier.cs` — `eval`/`source` invocation classification and
  command-substitution operand reviewability (established-idiom recognition).
- `Atoll.Api/Services/Security/Scanning/ShellArraySpans.cs` — array-assignment value spans and inert-data
  detection for tool mentions.
- `Atoll.Api/Services/Security/Scanning/ShellCommandPositions.cs` — decides whether a tool word sits in command
  position (an invocation) or is an argument/assignment data handed to another command.
- `Atoll.Api/Services/Security/Scanning/NetworkRuleExemptions.cs` — network-rule structural exemptions
  (`perl -e` text filters, shell script file arguments, network connector pipes).
- `Atoll.Api/Services/Security/Scanning/HiddenCharacters.cs` — hidden-codepoint detection and benign-context
  rules (ANSI escapes, zero-width characters, mojibake).
- `Atoll.Api/Services/Security/Scanning/PkgBuildSourceUrlScanner.cs` — inspects `source=` URLs (PKGBUILD only).
- `Atoll.Api/Services/Security/Scanning/HomographScanner.cs` — detects homograph spoofing in PKGBUILD metadata
  fields (PKGBUILD only).
- `Atoll.Api/Services/Security/Scanning/PackageBuildFileClassifier.cs` — decides which files are scannable.
- `Atoll.Api/Services/Security/Scanning/LocalSourceBinaryScanner.cs` — classifies binary source files.
- `Atoll.Api/Services/Security/PackageSecurityWorker.cs` (the polling worker),
  `Atoll.Api/Services/Security/Persistence/` (`IPackageSecurityRepository.cs`,
  `MongoPackageSecurityRepository.cs`, `PackageSecurityScanDocument.cs`), `PackageSecurityAccess.cs`,
  `PackageSecurityFilter.cs` (the `IEndpointFilter` gating content routes), and `Endpoints.cs` (route
  registration).

The `Scanning/` types are `internal static` with focused unit tests under `Atoll.Api.Tests/Security/Scanning/`; the
facade is covered end-to-end by `PkgBuildSecurityScannerTests`, which doubles as a regression fixture on a real-world
`shelly` PKGBUILD.

## Scope and threat model

Atoll is a private, read-only AUR mirror. The security layer is **defense-in-depth static analysis**, not a shell
sandbox. It detects:

- Downloads piped into a shell (`curl … | sh`) or an interpreter.
- Decoded or evaluated payloads (`base64 -d | bash`, `eval $(…)`).
- Writes outside the build roots (`> /etc/…`).
- Privilege escalation (`sudo`, `doas`, `run0`, `su`, …).
- Obfuscation hiding any of the above (quote-split tool names like `c''u''rl`, backslash escapes).
- Homograph spoofing: hidden/invisible characters in script content, and lookalike metadata values
  (`pkgname`, `depends`, `makedepends`, `url`, `source`) — invisible/combining characters, Latin mixed with
  Cyrillic/Greek/Armenian, fullwidth ASCII, confusables.
- Suspicious `source=` URLs pointing at raw executables/archives.
- Local source files shipped as ELF executables or binary blobs.

It does **not** defend against: malicious shell that avoids the matched patterns, malicious code hidden inside a
binary, supply-chain compromise of upstream sources, or anything that only becomes dangerous after the package is
built and installed. Treat `Verified` as "no obvious red flags", never as "safe".

With `Atoll:Security:Enabled=false` every package is served regardless of status, including previously `Flagged`
ones. Disabling the feature is a bypass, not a relaxed mode.

## Status model

Each retained package revision has a scan-state document in the `package-security-scans` collection, keyed by
`{packageName}:{revisionId}`. Revision IDs hash the package name plus sorted file names and per-file hashes, so the
same normalized snapshot maps to the same ID. A new head revision gets a fresh `Pending` document; previous revisions
keep their own state, so a flagged revision blocks only itself. Documents carry a denormalized `isHead` flag so the
gate can resolve the head scan without reading `packages`.

| Status | Meaning | Content served? |
| --- | --- | --- |
| `Pending` | No successful scan yet (newly seeded, re-scanned, or leased). | **Blocked** |
| `Verified` | Scan completed with no Critical/High findings. | Allowed |
| `Flagged` | Scan completed with at least one Critical or High finding. | **Blocked** |
| `Error` | Scan threw; blocked until a successful re-scan. | **Blocked** |

Severity ordering is `Info < Low < Medium < High < Critical`; only `Critical` and `High` flip a revision to
`Flagged`. The status-to-decision mapping lives in `PackageSecurityAccess.CheckAsync` and is the single place that
decides whether content is served. Leases, owners, and findings live only in `package-security-scans`; package
content documents stay free of scanner metadata.

## Scanner

`PkgBuildSecurityScanner` is deterministic and side-effect free: same input, same findings, no code executed. Every file
is first checked for binary content (whole-file, regardless of extension): recognized executable formats (ELF, PE/`MZ`)
are `Critical`, any other binary content — archives, inert media, certificate/signature files, undecodable text — is a
non-blocking `Medium` (`local-binary`), as is an ELF file named as a shared library (`*.so`, `*.so.N`). Script-like
files (the `PKGBUILD` plus `.sh`, `.bash`, `.install`, `.hook`, `.py`, `.pl`, `.rb`, `.service`, `.csh`, `.zsh`) are
then scanned line by line; other text files are ignored.

Each script line is processed as follows:

1. Shell comments are stripped, honoring single- and double-quote state.
2. The line is **de-obfuscated**: quote-splitting is collapsed (`c''u''rl` → `curl`), intra-word quotes
   (`c'u'rl` → `curl`) and backslash escapes are dropped. Quotes at word edges are kept — `'npm'` stays a quoted
   string, not an invocation.
3. Every rule matches against both the raw and the de-obfuscated line. A match that appears **only** on the
   de-obfuscated probe means the invocation was deliberately hidden and escalates to `Critical` — unless the match
   maps entirely inside quoted regions of the raw line (an escape-stripping artifact like echo'd `\$(sudo …)`, where
   the backslash prevents execution), which is dropped or kept at normal severity.

Matching is quote-aware: matches that cannot execute are dropped. Expansions (`$(…)`, backticks, `${!…}`) inside
single quotes or behind a backslash are literal text; double-quoted expansions still execute and stay flagged.
Redirects and `tee` targets are dropped when the operator sits inside any quoted string (`echo " >> /etc/…"` is
display text). A pipe inside a command substitution stays live even when the substitution is quoted —
`echo "$(curl … | sh)"` executes.

Heredoc bodies are tracked across lines. A quoted delimiter (`<<'EOF'`, `<<"EOF"`, `<<\EOF`) makes the body literal
data, suppressing the non-blocking expansion rules — unless the declaration pipes the body into a shell or
interpreter (`cat <<'EOF' | sh`). Blocking rules stay active inside bodies, except on `#` lines, where
`privilege-escalation` and `write-outside-build-root` never fire. Unquoted-delimiter bodies expand and are scanned
as ordinary lines.

### Rules

| Rule id | Severity (default) | What it detects |
| --- | --- | --- |
| `network-to-shell` | Critical | Downloader (`curl`, `wget`, `aria2c`, …) piped into a shell. |
| `decode-to-shell` | Critical | Decoder (`base64`, `xxd`, `openssl enc`, `printf`, `echo`) piped into a shell. |
| `eval-indirection` | Critical (Medium for established idioms) | `eval`/`source`/`.` in command position, fed by command substitution, backticks, or a payload. |
| `network-execution` | High | Downloader chained (pipe/`;`/`&&`) into a shell or interpreter (`python`, `perl`, `ruby`, `node`, `eval`). |
| `write-outside-build-root` | High (Medium in `.install` scriptlets and in scripts the PKGBUILD never invokes) | Redirect/`tee` into system paths (`/etc/`, `/usr/`, `/bin/`, …). |
| `privilege-escalation` | High (Medium in `.install` scriptlets and shell helper scripts) | Boundary-delimited `sudo`, `sudoedit`, `doas`, `pkexec`, `run0`, `su` in command position. |
| `hidden-character` | Critical (Medium for zero-width chars) | Bidi overrides/isolates and C0/C1 control bytes; zero-width chars (U+200B/C/D, U+FEFF) are Medium. |
| `homograph` | Medium | Spoofing in PKGBUILD metadata values. PKGBUILD only. |
| `command-substitution` | Medium | `$( … )` or backticks (non-blocking). |
| `variable-indirection` | Medium | Bash indirect expansion `${!var}` (non-blocking). |
| `suspicious-source-url` | Medium | A `source=` URL pointing at a raw executable/archive (`.exe`, `.msi`, `.bin`, `.zip`, …). PKGBUILD only. |
| `local-binary` | Critical (Medium for non-executable content and shared libraries) | A source file containing binary bytes; only recognized executable formats (ELF, PE/`MZ`) block, and an ELF named as a shared library (`*.so`, `*.so.N`) is packaged payload like an archive's contents. |

Privilege-escalation tools are matched as shell **words** (a shell boundary character before and whitespace after),
so `sudo` inside `pseudo` or `sudoku` is not flagged. A word must also be in **command position** — see
`Scanning/ShellCommandPositions.cs` — because a boundary-delimited name is not yet an invocation. Array-assignment
values (`depends=(mono curl … sudo …)`, including multi-line arrays) are tracked across lines: their words are
assigned data, never invoked, so tool mentions there are not findings — while command substitutions inside an array
(`depends=($(curl …))`) still execute and keep theirs.

### False-positive suppression

The corpus is overwhelmingly benign, so several exemptions downgrade or drop matches on plainly visible, non-executing,
or context-benign constructs. They were tuned against a full corpus audit (2026-08-27) and are pinned by the unit and
facade tests:

- **Quoted display text is not execution.** Pipe rules drop matches whose `|` sits inside a quoted string (uninstall
  help text, optdepends notes, usage strings). A quoted `eval` keyword or one used as an argument mention
  (`pkgdesc="An open source …"`) is not flagged.
- **Array assignments are data.** `name=( … )` values (dependency arrays, `source=`, plain shell arrays) assign
  words without invoking them, so `depends=(… sudo …)` or `… curl …` is not a finding; substitutions inside an
  array still execute and stay flagged. The value region can span lines and ends at the unquoted closing paren.
- **A tool name that is not in command position is not an invocation.** `ShellCommandPositions` walks back from the
  word to the command that governs it, so names used as arguments, list elements, or prose drop out: `cd sudo` walks
  into a directory (`ttf-sudo`), `install -Dm755 sudo "$pkgdir/usr/lib/sudos-eyes/sudo"` installs a file *named*
  `sudo`, `for _gsu in pkexec kdesu gksu` searches for binaries, `_install_module curl` passes the name to a function,
  and `avahi should be enabled first with: sudo …` is help text. Command position means: line start, after a separator
  (`;`, `&&`, `|`, `(`, `` ` ``, `{`, `}`), after a control word (`if`, `then`, `do`, `!`, `time`, `exec`, `eval`,
  `env`, …) or another privilege tool, or after the option list of a command that runs what it is handed (`python -m
  pip`, `xargs curl`, `ssh host sudo …`). `for`, `in`, `case` and `select` are deliberately excluded — their operand
  is a variable name or a word list. Options, numeric option values and `NAME=value` prefixes are stepped over, so
  `FOO=bar sudo make install` and `nice -n 10 sudo make install` still flag. The gate is applied to visible matches
  only: an obfuscated tool name keeps its `Critical` escalation wherever it sits, and a later live occurrence on the
  same line is still found (`cd sudo && sudo make install`).
- **Structural exemptions for visible constructs** (obfuscated matches keep their Critical escalation):
  `network-execution` drops a `perl` receiving its program from inline `-e`/`-E` code (the download is stdin *data*
  for the reviewable program), and `decode-to-shell` drops a pipe whose shell reads its script from a file argument
  (`echo yes | bash ./install.sh` — the executed code is the reviewable local file).
- **File-context downgrades.** ALPM runs `.install` scriptlets as root, so escalation tools and system writes there
  are redundant rather than dangerous: both rules downgrade to `Medium`. Packaged helper scripts
  (`.sh`/`.bash`/`.csh`/`.zsh`) get the same downgrade for `privilege-escalation` only — they run solely when invoked
  voluntarily. `write-outside-build-root` keeps High in helper scripts the PKGBUILD invokes (directly or via
  `install=`); that bucket holds the corpus's genuinely alarming content (NOPASSWD sudoers writes, root
  `authorized_keys` injection). PKGBUILD build-time invocations (`sudo make install`) run as the building user and
  stay High.
- **Shared libraries are packaged payload.** An ELF file named by the linker convention (`*.so`, `*.so.N[.N…]`) is
  loaded by other programs rather than executed directly, so it is the same trust class as the ELF binaries inside
  the vendored `.deb`/tar archives such packages also ship (Medium `local-binary-archive`/`suspicious-source-url`)
  and downgrades to a review-only `Medium`. Corpus pattern: committed compat libraries and plugin `.so` files
  (activinspire's `libre2.so.5`, hoffice's `libqt5im-nimf.so`, `libsteam_api.so`). The name is the signal — `e_type`
  cannot distinguish libraries from PIE executables — so renaming an executable to `*.so` gains only this downgrade
  and the file stays visible in the repository for review; any other ELF name keeps `Critical`.
- **Scripts the PKGBUILD never invokes cannot run.** A script the PKGBUILD mentions only in ways that cannot execute
  it — nowhere in its text, as a `source=`/checksum array entry, or staged into the build tree by
  `install`/`cp`/`mv`/`ln` (an explicit `$pkgdir`/`$DESTDIR` target or a relative destination, which lands inside
  the build tree) — never runs at build or install time, so its `write-outside-build-root` findings downgrade to
  `Medium`. Corpus patterns: maintainer-only docker build/release tooling (ferdium-bin's `dockerscript.sh`, referenced
  nowhere), packaged payload scripts that only execute later on the user's system (ccache-ext's
  `update-ccache-links.sh`, run by a pacman hook; crashplan-pro's `upgrade.sh`, run by a path-triggered systemd
  unit — staged via `install … bin/upgrade.sh` after a `cd $pkgdir`). The write stays visible for review, and
  obfuscated constructs still escalate to Critical. Any
  other mention — a typed or `bash` invocation, an `install=` scriptlet entry, a comment, a copy to a system path —
  counts as an invocation, including one after a data declaration or staging command on the same line. Staging outside
  the build tree is itself out-of-root behavior, and when there is no PKGBUILD to check against the conservative answer
  keeps the finding blocking. Evasion via glob or variable-based invocation remains possible and accepted.
- **Established `eval` idioms are Medium:** `eval echo …` / `eval printf …` with literal words plus
  tilde/variable expansion, and `eval $(…)` fed by a well-known environment emitter (`opam env`, `makepkg -g`,
  `dbus-launch`), a local file parser (`grep`, `awk`, `sed`, `cat`), or a local read-only monitor (`sensors`).
  Anything dynamic — `eval $(curl …)` — keeps blocking severity.
- **`hidden-character` tiers.** Complete ANSI escape sequences are skipped (they only affect display). Zero-width
  characters are Medium: they cannot make executed code differ from reviewed code, only make names display
  differently. Bidi overrides — the real trojan-source vector — and other control bytes stay Critical, except C1
  bytes adjacent to Latin-1 supplement characters (mojibake file names). A bare ESC outside a complete CSI sequence
  (e.g. OSC title escapes) stays Critical even inside quotes.
- **`homograph` scope.** PKGBUILD metadata values only, after comment stripping and quote removal; CJK/Hangul are
  excluded from the mixed-script check (they cannot spoof ASCII). Findings are Medium and do not block: the mirror
  displays raw metadata values, so lookalikes stay visible for review.

Adding or changing a shell rule is a one-line change to the `Rules` array in `ShellContentScanner` (or the
`PrivilegeEscalationTools` / `RiskyTools` arrays in the same file); `local-binary` and `homograph` are separate
whole-file/field-value checks. Which words count as an invocation is decided by the two word lists in
`ShellCommandPositions` (`CommandPositionWords`, `ArgumentExecutingCommands`) — a tool added to the rule arrays that
runs its arguments belongs in the latter too. Rule ids are persisted verbatim in stored findings, so renaming a rule
changes the ids visible in historical documents.

## Pipeline

The persisted `Pending` state is the durable work queue — there is no in-process queue:

1. A new revision is seeded or a rescan is requested (`POST /v1/packages/{name}/security/rescan`, optionally
   `?revision={sha}`); both call `MarkPendingAsync`, which upserts the `(package, revision)` document to `Pending`,
   clears prior findings/lease, and stamps `requiredPolicyVersion` with the enqueuing scanner's current policy
   version (monotonic: an existing requirement is never lowered). On public instances set
   `Atoll:Mutations:Enabled=false` to make the rescan endpoint return `403` and hide the UI button (this also applies
   to the seed and delete endpoints).
2. `PackageSecurityWorker` runs `ScannerConcurrency` poll loops. Each atomically (`FindOneAndUpdate`) leases one
   `Pending` document whose lease has expired or is unset **and whose `requiredPolicyVersion` the worker's policy
   satisfies**, stamping `leaseUntil = now + 5m`. An older worker can never claim work that a newer deployment
   marked as requiring a newer policy.
3. The worker re-reads the claimed revision. If it is no longer retained (aged out of `MaxRevisions`, or the package
   was deleted) the claim is deleted — a result must never be written for content that can no longer be served.
4. Otherwise the files are scanned and the result written with `CompleteScanAsync`, guarded by
   `(id, pending, leaseOwner, requiredPolicyVersion <= worker policy)` so only the claim owner can complete it and a
   requirement raised mid-scan rejects the write. The method returns whether MongoDB modified a document; on a
   rejected (stale) write the worker logs the discarded result and does not count it as completed or errored — a
   policy mismatch during rollout is expected, not a scan failure. The claim is keyed by revision, so a head swap
   mid-scan does not disturb the in-flight scan.
5. If the scan throws, the revision is recorded `Error` through the same fenced write.

Leases make the queue crash-safe: if a worker dies mid-scan, the lease expires and another worker (or the same
instance after restart) reclaims it after 5 minutes.

### Policy versioning and startup

Persisted results are stamped with a monotonically increasing integer policy version
(`PkgBuildSecurityScanner.CurrentPolicyVersion`, stored as `policyVersion`). Increment it whenever a scanner change
requires existing verdicts to be recomputed; versions must never be reused or decremented.

Every pending document additionally carries `requiredPolicyVersion` — the minimum scanner policy allowed to claim
and persist it. It is retained after completion and only ever raised (`$max` semantics in both enqueue and
reconciliation, so an older reconciler cannot lower work a newer one already claimed). A missing value is treated as
legacy/unconstrained during the rollout; every enqueue path and reconciliation populates it.

On startup, before polling, the worker:

1. **Requeues outdated scans** (`RequeueOutdatedAsync`): completed/`Error` documents whose `policyVersion` is null
   (legacy) or lower than the current version are reset to `Pending` (clearing findings, timestamps, and leases) and
   their requirement raised to the current version; pending documents whose `requiredPolicyVersion` is null or lower
   are raised to the current version and their lease cleared. Clearing a lease fences a lower-version worker that
   claimed the document before the requirement was raised. Results from a *newer* policy and requirements already
   above the current version are preserved, so an older worker cannot downgrade them during a rolling deployment.
2. **Backfills missing scan documents** (`EnsureExistingPackagesArePendingAsync`): computes the set difference
   between seeded packages and existing scan documents and upserts a `Pending` entry only for the missing ones —
   without overwriting completed scans.

Startup reconciliation plus claim/completion fencing covers the rolling-deployment race: a stale result written
before reconciliation is requeued by it, a result written after the requirement was raised is rejected by the
completion fence, and an older policy-aware reconciler cannot lower a newer requirement.

Configuration is validated by Data Annotations at startup. The worker is a hosted service registered in
`Program.cs`; it starts with the API and stops on shutdown.

## Gating

`PackageSecurityAccess.CheckAsync` is the single decision point, enforced by `PackageSecurityFilter` on the
content-serving route group in `Endpoints.cs` — head content, a requested revision, and both Git Smart HTTP routes:

| Condition | Result |
| --- | --- |
| Security disabled (`Enabled=false`) | Allow everything, including previously Flagged. |
| Package does not exist | Allow (the route returns 404 downstream). |
| Status `Verified` | Allow. |
| Status `Pending`, or no scan document | Block — `security_status_pending`. |
| Status `Flagged` | Block — `security_status_flagged`. |
| Status `Error` | Block — `security_scan_error`. |

Blocked requests return `403 Forbidden` with an RFC 9457 `application/problem+json` body and a non-sensitive
`reason` extension code; no file content or finding detail is leaked. Version history and the security status
endpoint stay ungated (metadata and scan summaries only).

**UI exception:** the Blazor Files tab is deliberately *not* gated — it serves flagged revisions read-only with a
warning banner, so users can inspect the content that triggered the findings.

**Git materialization is scan-status aware:** the bare repository is materialized from `Verified` revisions only, so
a `Flagged`/`Pending`/`Error` historical revision cannot be reached via `git clone` + `git checkout <sha>`. The
`.atoll-head` marker embeds every retained revision id and its scan status; any status or history change (or
toggling security) invalidates the marker and triggers a lazy rebuild on the next Git request.

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
| `Enabled` | `true` | bool | Master switch. `false` makes `CheckAsync` allow everything and the worker exit without polling. |
| `ScannerConcurrency` | `4` | 1–64 | Number of parallel poll/scan loops; also bounds startup backfill parallelism. |
| `PollIntervalMs` | `100` | 100–300000 | Delay between poll attempts when nothing was claimed; trades load for scan latency. |

The lease duration is fixed at 5 minutes in `PackageSecurityWorker` and is not configurable. The
`atoll_securityscan_pending` gauge refreshes every 30 seconds, independent of `ScannerConcurrency`.

## Observability

`GET /metrics` serves Prometheus-format OpenTelemetry metrics; the `atoll_securityscan_*` instruments are backed by
`SecurityScanStatusStore` and updated by the worker:

| Metric | Meaning |
| --- | --- |
| `atoll_securityscan_completed_total` | Scans that reached a terminal status. |
| `atoll_securityscan_verified_total` / `atoll_securityscan_flagged_total` / `atoll_securityscan_errored_total` | Terminal-status breakdown. |
| `atoll_securityscan_dropped_total` | Claims dropped because the revision aged out of retained history before scanning. |
| `atoll_securityscan_pending` | Backlog depth (number of `Pending` documents), refreshed every 30 s. |
| `atoll_securityscan_last_finished_timestamp` | Unix time of the last completed or errored scan. |

Content is not served until the head revision is scanned, so compare `atoll_securityscan_pending` against seed and
refresh throughput on the same endpoint to see whether the scanner keeps up with ingestion.

Each completed scan logs `Security scan for {PackageName} revision {RevisionId} -> {Status} ({FindingCount}
findings).`; failures log a warning and record `Error`. Useful ad-hoc queries on `package-security-scans`:

- Blocked revisions: `{ status: { $in: ["Pending", "Flagged", "Error"] } }`
- All scan state for one package: `{ packageName: "<name>" }`
- Stuck leases: `{ status: "Pending", leaseUntil: { $lt: <now> } }` (reclaimable; should clear on the next poll)

## Manual verification

These checks need only `curl` (or a Git client) and read access to the running API.

1. **Gating and reason codes** — seed a PKGBUILD that pipes a download into a shell, then:
   - `GET /v1/packages/$NAME/security` moves `Pending` → `Flagged` with a non-zero `findingCount`.
   - `GET /v1/packages/$NAME` and `GET /packages/$NAME.git/info/refs?service=git-upload-pack` return `403` with
     `"reason":"security_status_flagged"`.
   - `GET /v1/packages/$NAME/versions` returns `200` (history is metadata, not content).
2. **Clean package verifies** — seed a minimal PKGBUILD with none of the matched patterns; the status becomes
   `Verified` and `GET /v1/packages/$NAME` returns `200`.
3. **Rescan re-queues** — `POST /v1/packages/$NAME/security/rescan` (optionally `?revision=<sha>`) returns `202`; the
   revision returns to `Pending` and resolves again.
4. **Lease recovery** — mark a package `Pending` via rescan, stop the worker (scale the instance to zero or block
   the DB — setting `Enabled=false` only prevents polling), wait past `leaseUntil`, restart. The stuck-lease query
   above must clear on its own after restart.

## Limitations and follow-ups

- **Static analysis only.** Shell and obfuscation outside the matched patterns are not detected, and binary contents
  cannot be inspected for malicious behavior. Do not treat `Verified` as a guarantee.
- **Binary detection runs on UTF-8-decoded strings.** Invalid byte sequences collapse to U+FFFD before inspection.
  This catches ELF/NUL/control content, but magic matching is weaker than raw-byte detection (which would require
  the seed paths to surface raw bytes).
- **Heredoc prose can still block.** Only the non-blocking expansion rules are suppressed in quoted-delimiter bodies;
  `sudo`/redirect-looking prose can still yield blocking findings. Extending suppression requires safely handling
  pipe-to-installer patterns first.
- **Command position is a heuristic over one line.** The governing word is found by walking back over options, numeric
  values and `NAME=value` prefixes, so an option value that is itself a word ends the walk there (`env -u FOO sudo x`)
  and a word after an unquoted `$( … )` stays in command position — dropping it would lose the real
  `VAR=$(probe) sudo cmd`. A command that executes its arguments but is absent from `ArgumentExecutingCommands` hides
  the tool it is handed, exactly as an inline `bash -c 'sudo …'` always did.
- **Homograph checks are field-scoped.** Only single-line `pkgname`/`depends`/`makedepends`/`url`/`source` values;
  multi-line arrays, other fields, and free prose are out of scope. Legitimate internationalized names can trip the
  mixed-script check (accepted — rare in the corpus), and single-script spoofing outside the confusables table is
  not detected.
- **No manual override.** There is no `ForceVerified`/`ForceBlocked` state for reviewed packages.
- **No source-host policy.** `suspicious-source-url` is syntactic only; there is no allow/deny list for domains.
- **Git history is the verified subset.** A Verified → Flagged flip makes the old commit unreachable (dangling
  object on disk); unreachable objects are never advertised or fetchable, but a periodic `git gc --prune` in the
  repositories directory reclaims the space.
- **Single-instance assumption.** Leases are crash-safe within one instance but unvalidated for multiple API
  replicas; see `ARCHITECTURE.md`.

## Alignment with shelly-alpm

The scanner shares lineage with the security validators of
[shelly](https://github.com/Seafoam-Labs/Shelly-ALPM) (Zig Arch package manager) — the risky/privilege tool lists
were originally identical. The two enforce differently: shelly advises a human who approves the build, while Atoll
auto-blocks serving on High/Critical, so shelly's noisier rules must not be ported wholesale. This mapping is the
reference for a future "shelly changed, catch up" task; the tool lists are the likeliest drift point (plain arrays
in `ShellContentScanner.cs`, trivially diff-able against shelly's).

Last full comparison: shelly commit `8988d056` (2026-08-21). Re-run when shelly's validators change meaningfully —
see `post_install_validator.zig`, `homograph_validator.zig`, `local_source_validator.zig`, and
`parser/shell_scan.zig` under `Shelly.PackageManager/src/pkgbuild/`.

| Shelly validator/concept | Atoll counterpart | Divergence (intentional) |
| --- | --- | --- |
| Risky tools | `risky-tool` (Medium) | Atoll adds quoted-region, array-assignment, and command-position exemptions. |
| Privilege tools | `privilege-escalation` | Same, plus quote/array/command-position exemptions, obfuscation escalation, and the scriptlet/helper context downgrades. |
| Bare `eval` token → critical | `eval-indirection` | Atoll requires a dynamic operand in command position; established idioms are Medium. |
| Decode-to-shell | `decode-to-shell` | Atoll superset (`openssl enc`, more shell targets); quoted-display and script-file-argument pipes suppressed. |
| — | `network-to-shell` / `network-execution` | Atoll-only; quoted-display pipes suppressed, `perl -e` text filters exempted. |
| Command substitution / indirection (naive) | `command-substitution` / `variable-indirection` | Atoll is quote-aware and heredoc-aware; shelly matches naive substrings. |
| — | `write-outside-build-root` | Atoll-only; scriptlet and never-invoked-script writes downgraded, invoked helper scripts keep High. |
| `homograph_validator.zig` | `homograph` | Ported conceptually; field-scoped to metadata, CJK/Hangul excluded, Medium not blocking. |
| `local_source_validator.zig` (ELF, first 64 bytes of `source=` files) | `local-binary` | Atoll checks every file, whole content; blocks only on executable magic. |
| Obfuscation normalization (edge + intra-word quotes) | `NormalizeForMatching` (intra-word only) | Edge-quote stripping would re-introduce quoted-string FPs in Atoll's blocking model. |
| `shell_scan.zig` segmentation | `ShellSyntax` quoted masks + `ShellHeredocs` heredoc tracking | Adopted for FP suppression; shelly's validators don't suppress on it. |
| URL validation | `suspicious-source-url` | Host-only extension matching, test-pinned. |

Not adopted: install-script scope labels (nice-to-have); review digest/TOCTOU and sandboxing (not applicable — Atoll
never executes or live-reviews content).
