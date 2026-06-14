# Atoll

> Atoll - A ring-shaped coral reef; a community ecosystem for arch packages.

Minimal API that mirrors Arch Linux AUR package metadata and provides fast package search endpoints.

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
docker compose up --build
```

## Endpoints

- `GET /health` - health check
- `GET /packages?query=<value>&by=Name|Desc|Prov` - package search
- `GET /metrics` - service metrics

- Name: search by package exact name
- Desc: search by package words (Name, Description, Keywords)
- Prov: search by package provides


## Configuration

Main settings in `Atoll.Api/appsettings.json`:

- `DataFile`
- `DataFileUrl`
- `RefreshIntervalMinutes`
