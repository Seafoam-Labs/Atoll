# Development Setup

How to get Atoll building and running locally. This covers the tooling you need on the
machine (Tailwind, .NET, Docker, Terraform) — the shortest possible path for each. For
what each piece does, see [ARCHITECTURE.md](ARCHITECTURE.md); for running/production
details, see [DEPLOYMENT.md](DEPLOYMENT.md).

## One-time prerequisites

| Tool | Required? | Notes |
| --- | --- | --- |
| .NET SDK 10 | Always | `net10.0`; required to build and run |
| `git` | Always | the app shells out to `git upload-pack`; must be `git` on `PATH` |
| Tailwind CSS v4 CLI | To build CSS | standalone binary; v4 is used, see below |
| Docker / Docker Compose | For the bundled stack | MongoDB + the observability stack |
| Terraform | Only for AWS deploys | `terraform/` |
| MongoDB | Via Docker (recommended) | or a running instance for `RequiresMongo` tests |

The Tailwind CLI is only needed to compile `wwwroot/app.css` → `wwwroot/app.min.css`
(gitignored). The build target resolves the binary from `$HOME/.local/bin/tailwindcss`,
falling back to `tailwindcss` on `PATH`. Install the standalone binary matching the
version pinned in the Dockerfile (`Atoll.Api/Dockerfile`, `TAILWIND_VERSION`):

```bash
TAILWIND_VERSION=4.3.3
curl -fsSL "https://github.com/tailwindlabs/tailwindcss/releases/download/v${TAILWIND_VERSION}/tailwindcss-linux-x64" \
  -o "${HOME}/.local/bin/tailwindcss"
chmod +x "${HOME}/.local/bin/tailwindcss"
```

> macOS: use `tailwindcss-macos` (arm64/x64) from the same release. Windows: use
> `tailwindcss-windows-x64.exe` and adjust the path in `Atoll.Api.csproj`.

### Why Tailwind CSS?

Atoll uses Tailwind CSS v4 to maintain a unified, design system across all Blazor pages (catalog, package details, file
viewer, and operational status dashboard):

- **Consistent design system:** Tokens defined in `wwwroot/app.css` (`@theme`) centralize the color
  palette (brand cyan, surfaces/canvas, ink, security statuses, finding severity levels) and typography,
  ensuring all UI views share coherent accents, contrasts, and spacing.
- **Composable UI component ecosystem:** Standard Tailwind primitives allow reusing community and
  Tailwind UI patterns (such as stacked layouts, stats grids, description lists, card headings,
  and badges) directly in Razor components or extracting them into shared `@layer components` classes.
- **Zero Node.js dependency:** Using the standalone Tailwind CLI binary avoids requiring `node`, `npm`,
  or a frontend bundler toolchain in local development or Docker builds.

## Build & run

```bash
dotnet build        # compiles Tailwind CSS first (needs the CLI)
dotnet run --project Atoll.Api
```

Then open:

- Web UI: <http://localhost:5290>
- OpenAPI: <http://localhost:5290/openapi/v1.json>

`dotnet run` uses the default `Development` profile. The app expects MongoDB; see Docker
below for the bundled stack. When running via `docker compose`, the app is on port `8080`
instead.

### Skipping the Tailwind build

When the Tailwind CLI is not installed (e.g. on a throwaway box), pass `-p:SkipTailwind=true`
so the build skips CSS compilation. The same flag applies to `dotnet test`:

```bash
dotnet build -p:SkipTailwind=true
dotnet test  -p:SkipTailwind=true
```

Without it, the `BuildTailwindCss` MSBuild target runs `BeforeTargets="PrepareForBuild"` and
will fail if the CLI is missing.

## Docker

`compose.yaml` runs the full local stack:

```bash
docker compose up --build
```

- `atoll` — the app, exposed on **port 8080** (UI + API)
- `mongo` — MongoDB 8.3 on port 27017
- `lgtm` — observability stack (OTel Collector + Prometheus + Grafana), **opt-in** via profile:

```bash
docker compose --profile observability up --build
```

Grafana: <http://localhost:3000> (`admin` / `admin`); the pre-provisioned "Atoll" dashboard is
set as home. Uncomment the `OTEL_EXPORTER_OTLP_ENDPOINT` / `OTEL_METRIC_EXPORT_INTERVAL`
lines in `compose.yaml` to point the app at it.

> `app.min.css` is built by the `Atoll.Api` Dockerfile (the image pins its own `tailwindcss`
> binary), so the Docker build does not need your local CLI. The generated file is gitignored.

## Tests

Fast tests (no Docker, no MongoDB, no Git binary):

```bash
dotnet test --filter "Category!=RequiresGit&Category!=RequiresMongo"
```

Git-backed tests (needs system `git` CLI on `PATH`):

```bash
dotnet test --filter "Category=RequiresGit"
```

Mongo-backed tests (needs a MongoDB; the bundled compose one works):

```bash
dotnet test --filter "Category=RequiresMongo"
```

Full suite:

```bash
dotnet test
```

Skip the Tailwind CSS build during test runs with `-p:SkipTailwind=true`.

## Terraform (AWS deploys only)

The AWS infrastructure lives in `terraform/`; see [DEPLOYMENT.md](DEPLOYMENT.md) for the
architecture and the full pipeline. To work with it locally:

```bash
terraform -chdir=terraform init
terraform -chdir=terraform plan
```

The bootstrap stack (`terraform/bootstrap/`) is one-time per account (state bucket, lock
table, OIDC provider, deploy role); its state stays local (gitignored). The main stack's
state lives in the remote bucket created by bootstrap. Real application deploys go through
GitHub Actions, not a dev machine.

## Generated / ignored files

- `wwwroot/app.min.css` — Tailwind output, gitignored
- `**/.terraform/`, `*.tfstate*`, `*.tfplan` — Terraform state/plans, gitignored
- `Atoll.Api/data/` — runtime data (repos, mirror cache), gitignored
- `packages-meta-ext-v1.json` — downloaded AUR metadata dump, gitignored
