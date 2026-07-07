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
# Uses port 8080
docker compose up --build
```

## Garage S3 Storage (Development)

Atoll supports using [Garage](https://garagehq.deuxfleurs.fr/) as an S3-compatible storage backend. A local Garage instance is included in `compose.yaml`.

To run the app from your IDE using Garage S3 storage:

1. Start the Garage service using Docker Compose:

   ```bash
   docker compose up -d garage
   ```

2. Configure your IDE or run command to use the `Garage` environment so that `Atoll.Api/appsettings.Garage.json` is loaded:

   - **dotnet CLI**:

     ```bash
     dotnet run --project Atoll.Api --environment Garage
     ```

   - **IDE**:
     - Add `ASPNETCORE_ENVIRONMENT` = `Garage` as Environment Variable (launchSettings.json)

## Endpoints

- `GET /health` - health check
- `GET /search?query=<value>&by=name|words|provides` - package search
- `GET /metrics` - service metrics

**by** parameter:

- `name`: search by package exact name
- `words`: search by package words (Name, Description, Keywords)
- `provides`: search by package provides

## Configuration

Main settings in `Atoll.Api/appsettings.json` and in `Atoll.Api/AtollOptions.cs`.

Also, `compose.yaml` contains example for using environment variables.
