# Atoll

> A ring-shaped coral reef; a community ecosystem for arch packages.

Atoll is a self-hosted Arch Linux AUR mirror and package registry. It mirrors AUR metadata, stores package revision
history, exposes searchable package metadata, and serves package content over Git Smart HTTP with a built-in Blazor UI.

## What this project includes

- AUR metadata indexing and fast in-memory search
- Package seeding, version history, and file browsing
- Git-compatible clone/fetch access for seeded packages
- AUR RPC v5 and standard clone URLs for yay/paru compatibility
- Versioned REST API under `/v1/…` (URL-segment versioning; RPC and Git Smart HTTP stay protocol-fixed)
- Security-gated content access for refreshed or newly seeded revisions
- Background workers for sync, refresh, and scanning
- Local web UI for catalog, package details, file views, and status

## Quick start

### Requirements

- .NET SDK 10
- Docker (optional, for the bundled stack)

### Run locally

```bash
dotnet run --project Atoll.Api
```

Then open:

- Web UI: <http://localhost:5290>
- OpenAPI: <http://localhost:5290/openapi/v1.json>

### Run with Docker

```bash
docker compose up --build
```

This starts the API, MongoDB, and the local observability stack. The UI and API are exposed on port `8080`, and Grafana
is available at `http://localhost:3000` with the default login `admin` / `admin`.

## Project layout

- `Atoll.Api/` — ASP.NET Core application and Blazor UI
- `docs/` — the canonical source for architecture, sync, security, and deployment details
- `observability/` — Grafana dashboards and OTLP config
- `terraform/` — AWS deployment and infrastructure definitions

## Detailed documentation

For important, up-to-date implementation and operations details, use the docs in `docs/`:

- [Architecture overview](docs/ARCHITECTURE.md) — system design, storage model, API surface, and architecture decisions
- [Development setup](docs/DEVELOPMENT.md) — local tooling: Tailwind CLI, Docker, Terraform, and test/build flags
- [Package seeding and refresh](docs/SYNC.md) — direct/bulk seeding, refresh behavior, config, and operational notes
- [Using yay and paru](docs/AUR_HELPERS.md) — helper configuration, RPC/Git compatibility, and limitations
- [Package security scanning](docs/SECURITY.md) — threat model, scan rules, queueing, and content gating
- [Deployment](docs/DEPLOYMENT.md) — AWS/GitHub Actions and Terraform setup

## Configuration

Main runtime configuration is in:

- `Atoll.Api/appsettings.json`
- `Atoll.Api/AtollOptions.cs`
- `compose.yaml`

For most feature-specific configuration, prefer the linked docs over the README.

### Environment variables

Some important environment variables are listed below.

| Variable | Values | Description |
| --- | --- | --- |
| `Atoll__Mongo__ConnectionString` | MongoDB connection string | Connection string for the MongoDB instance (e.g. `mongodb://localhost:27017`). |
| `Atoll__DataSource__RefreshIntervalMinutes` | Minutes (`5` by default) | Poll interval for the conditionally downloaded AUR metadata archive. |
| `Atoll__DataSource__PruneDeletedPackages` | `true`, `false` | Removes seeded packages absent from a successfully parsed AUR snapshot. See [SYNC.md](docs/SYNC.md). |
| `Atoll__Seed__Mode` | `Off`, `Direct`, `Bulk` | Controls how packages are seeded from AUR: disabled, direct fetch, or bulk import. See [SYNC.md](docs/SYNC.md). |
| `Atoll__Refresh__Enabled` | `true`, `false` | Enables or disables background refresh of already-seeded package content. |
| `Atoll__Security__Enabled` | `true`, `false` | Enables or disables security scanning and content gating. See [SECURITY.md](docs/SECURITY.md). |
| `Atoll__Mutations__Enabled` | `true`, `false` | Allows mutating operations. Set to `false` when exposing Atoll read-only on public networks. |
| `Atoll__Ui__ExternalBaseUrl` | URL (`http://localhost:5290` by default) | Public base URL of the instance used for UI links and Git clone instructions. |

Note: double underscores (`__`) are the standard .NET convention for nesting configuration sections, so
`Atoll__Seed__Mode` maps to `Atoll:Seed:Mode` in `appsettings.json`.

## Tests

Fast tests without Docker:

```bash
dotnet test --filter "Category!=RequiresGit&Category!=RequiresMongo"
```

Mongo-backed tests:

```bash
dotnet test --filter "Category=RequiresMongo"
```

Full suite:

```bash
dotnet test
```

## Notes

Atoll is intended for trusted private deployments. Search and metadata are public by design, but content access is gated
and should not be exposed broadly without network controls.

## Inspiration

Atoll was inspired by [faur](https://github.com/fosskers/faur), a community-driven AUR mirror project made in Clojure by
[fosskers](https://github.com/fosskers).
