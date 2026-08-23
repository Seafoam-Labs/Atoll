# Atoll

> A ring-shaped coral reef; a community ecosystem for arch packages.

Atoll is a self-hosted Arch Linux AUR mirror and package registry. It mirrors AUR metadata, stores package revision
history, exposes searchable package metadata, and serves package content over Git Smart HTTP with a built-in Blazor UI.

## What this project includes

- AUR metadata indexing and fast in-memory search
- Package seeding, version history, and file browsing
- Git-compatible clone/fetch access for seeded packages
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
- [Package security scanning](docs/SECURITY.md) — threat model, scan rules, queueing, and content gating
- [Deployment](docs/DEPLOYMENT.md) — AWS/GitHub Actions and Terraform setup

## Configuration

Main runtime configuration is in:

- `Atoll.Api/appsettings.json`
- `Atoll.Api/AtollOptions.cs`
- `compose.yaml`

For most feature-specific configuration, prefer the linked docs over the README.

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
