# Atoll

> Atoll - A ring-shaped coral reef; a community ecosystem for arch packages.

Minimal API that mirrors Arch Linux AUR package metadata, manages package versions and history, and provides fast
package search endpoints.

## Requirements

- .NET SDK 10
- Docker (optional)

## Run

```bash
dotnet run --project Atoll.Api
```

- API base URL: `http://localhost:5290`
- OpenAPI URL: `http://localhost:5290/openapi/v1.json`

## Docker

```bash
# Uses port 8080
docker compose up --build
```

## Storage

MongoDB is Atoll's authoritative store for AUR metadata, package metadata, revision snapshots,
and operational state. The in-memory search index and on-disk Git repositories are rebuildable
caches. Configure collections under `Atoll:Mongo` in `appsettings.json`.

For the storage layout, retention limits, BSON-size handling, and cache considerations, see
[Architecture](docs/ARCHITECTURE.md#state--storage).

## Endpoints

### Health

- `GET /health`, `HEAD /health` - basic liveness check

### Metrics

- `GET /metrics` - service metrics

### Search

- `GET /search?query=<value>&by=name|words|provides` - package search

**by** parameter:

- `name`: search by package exact name
- `words`: search by package words (Name, Description, Keywords)
- `provides`: search by package provides

### Packages

- `GET /packages` - List all packages
- `POST /packages/{name}/seed` - Seed package from AUR (returns `409 Conflict` if package already exists)
- `GET /packages/{name}` - Get specific package (head revision)
- `GET /packages/{name}/versions` - Get package versions
- `GET /packages/{name}/versions/{sha}` - Get specific package version
- `DELETE /packages/{name}` - Delete package

### Git Smart HTTP

Package repositories are exposed over the [Git Smart HTTP protocol](https://git-scm.com/docs/http-protocol), so any
seeded package can be cloned directly:

```bash
git clone http://localhost:5290/packages/{name}.git
```

Underlying endpoints (used by the Git client itself, rarely called by hand):

- `GET /packages/{name}.git/info/refs?service=git-upload-pack` - ref advertisement
- `POST /packages/{name}.git/git-upload-pack` - upload-pack negotiation and pack transfer

Only `git-upload-pack` (fetch/clone) is supported; `git-receive-pack` (push) is not.

## Hosted Services

Background services run automatically when the application starts.

### Package Index Worker

Downloads the AUR metadata dump, persists it to MongoDB, and rebuilds the in-memory search
index. On startup it hydrates from MongoDB; an empty database produces an empty index until the
first refresh. Configure the interval with `Atoll:DataSource:RefreshIntervalMinutes`.

### Package Seed and Refresh Workers

`Atoll:Seed:Mode` selects exactly one automated seed strategy: `Direct` (the default), `Bulk`,
or `Off`. The optional refresh worker (`Atoll:Refresh:Enabled=true`) independently updates
already-seeded packages. New revisions remain unavailable until security scanning completes.

See [Package seeding and refresh](docs/SYNC.md) for mode selection, configuration, mirror-cache
requirements, metrics, and verification. For Docker, set `Atoll__Seed__Mode` and use the
corresponding example settings in `compose.yaml`.

## Configuration

Main settings are defined in `Atoll.Api/appsettings.json` and `Atoll.Api/AtollOptions.cs`.
`compose.yaml` shows their environment-variable form. Detailed worker and security settings are
in [SYNC.md](docs/SYNC.md) and [SECURITY.md](docs/SECURITY.md), respectively.

## Tests

The test suite is split into tiers so fast unit/contract tests don't require Docker.

- **Fast tier** (default): in-memory fakes and contract tests. No Docker needed.

  ```bash
  dotnet test --filter "Category!=RequiresGit&Category!=RequiresMongo"
  ```

- **Mongo tier**: exercises `MongoPackageRepository` and `AurMetadataRepository` against a real
  MongoDB spun up with [Testcontainers](https://testcontainers.com/) (`mongo:8.3.7`, matching
  `compose.yaml`). Requires a running Docker daemon; skips gracefully if unavailable.

  ```bash
  dotnet test --filter "Category=RequiresMongo"
  ```

- **Full suite**: everything, including tests that need the real `git` CLI.

  ```bash
  dotnet test
  ```

Each Mongo test uses its own database (`atoll-test-*`) and drops it on teardown, so tests are
isolated from each other and from the app's runtime database.
