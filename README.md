# Atoll

> Atoll - A ring-shaped coral reef; a community ecosystem for arch packages.

Minimal API that mirrors Arch Linux AUR package metadata, manages package versions and history, and provides fast package search endpoints.

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

## MongoDB Storage (Packages)

Package data (current files and revision history) is stored in MongoDB.

Configure MongoDB under `Atoll:Mongo` in `appsettings.json`.

`MaxRevisions` caps embedded revision history per package. `MaxFileBytes` rejects
oversized files at seed time. Keep both conservative to stay under MongoDB's
16 MB document size limit.

## MongoDB Storage (AUR Metadata)

The AUR package dump (`packages-meta-ext-v1.json.gz`) is downloaded,
decompressed in memory, and stored as typed documents in MongoDB. The search
index is rebuilt from MongoDB on startup and on each refresh.

The AUR metadata collection is configured under `Atoll:Mongo:Collections:AurMetadata`
(shown above), alongside the user-package collection (`Collections:Packages`).

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

Package repositories are exposed over the [Git Smart HTTP protocol](https://git-scm.com/docs/http-protocol), so any seeded package can be cloned directly:

```bash
git clone http://localhost:5290/packages/{name}.git
```

Underlying endpoints (used by the Git client itself, rarely called by hand):

- `GET /packages/{name}.git/info/refs?service=git-upload-pack` - ref advertisement
- `POST /packages/{name}.git/git-upload-pack` - upload-pack negotiation and pack transfer

Only `git-upload-pack` (fetch/clone) is supported; `git-receive-pack` (push) is not.

## Hosted Services

Two background services run automatically when the application starts.

### Package Index Worker

Periodically downloads the AUR package dump, persists the parsed packages to
MongoDB, and rebuilds the in-memory search index. On startup the index is
hydrated from MongoDB; if MongoDB is empty, the API starts with empty indexes
and waits for the first refresh. The interval is controlled by
`Atoll:DataSource:RefreshIntervalMinutes`.

### Package Seed Worker

Iterates over the package index and seeds missing packages from the AUR into MongoDB. A delay between each seed request avoids rate-limiting:

```json
"Atoll": {
  "Seed": {
    "SeedDelayMs": 1000
  }
}
```

`SeedDelayMs` accepts values between **100** and **60000** milliseconds (default: **1000**).

## Configuration

Main settings in `Atoll.Api/appsettings.json` and in `Atoll.Api/AtollOptions.cs`.

Also, `compose.yaml` contains example for using environment variables.
