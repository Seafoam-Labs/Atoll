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

Configure MongoDB under `Atoll:Mongo` in `appsettings.json`:

```json
"Atoll": {
  "Mongo": {
    "ConnectionString": "mongodb://localhost:27017",
    "Database": "atoll",
    "PackagesCollection": "packages",
    "MaxRevisions": 10,
    "MaxFileBytes": 5242880
  }
}
```

`MaxRevisions` caps embedded revision history per package. `MaxFileBytes` rejects
oversized files at seed time. Keep both conservative to stay under MongoDB's
16 MB document size limit.

## Endpoints

### Health

- `GET /health`, `HEAD /health` - health check

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

## Configuration

Main settings in `Atoll.Api/appsettings.json` and in `Atoll.Api/AtollOptions.cs`.

Also, `compose.yaml` contains example for using environment variables.
