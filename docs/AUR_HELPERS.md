# Using yay and paru with Atoll

Atoll implements the AUR RPC v5 and Git Smart HTTP surfaces used by AUR helpers. Set the helper's AUR base URL to the
public root of the Atoll instance (without a trailing `/`).

The examples below use `http://localhost:5290`.

## yay

Configure both the Git base URL and the RPC endpoint, then save them to yay's configuration:

```bash
yay \
  --aururl http://localhost:5290 \
  --aurrpcurl http://localhost:5290/rpc \
  --save
```

You can also pass the same options for a single command without `--save`:

```bash
yay -S package-name \
  --aururl http://localhost:5290 \
  --aurrpcurl http://localhost:5290/rpc
```

## paru

Set `AurUrl` in `~/.config/paru/paru.conf`:

```ini
[options]
AurUrl = http://localhost:5290
```

Or override it for one command:

```bash
paru -S package-name --aururl http://localhost:5290
```

Paru derives both `http://localhost:5290/rpc` and `http://localhost:5290/{pkgbase}.git` from this value.

## Compatibility and behavior

Atoll supports:

- Legacy RPC requests at `/rpc?v=5&type=…`, including repeated `arg[]` parameters.
- Path-style RPC requests under `/rpc/v5/…`.
- `info`/`multiinfo`, `search`, `msearch`, `suggest`, and `suggest-pkgbase`.
- All aurweb v5 search fields: `name`, `name-desc`, `maintainer`, `comaintainers`, dependency/relation fields,
  `groups`, and `submitter`.
- AUR-compatible Git clone URLs at `/{pkgbase}.git`. The existing Atoll URLs at `/packages/{name}.git` remain
  available.
- Split packages: a `pkgbase` Git request is resolved to a seeded package belonging to that base.

RPC searches the full mirrored metadata catalog, but Git content exists only for packages that Atoll has seeded. A
helper can therefore find an unseeded package and then receive `404 Not Found` while cloning it. Choose a seed mode
appropriate for the deployment or seed the package through Atoll before installing it. See
[Package seeding and refresh](SYNC.md).

When security scanning is enabled, Git access remains blocked until the selected seeded revision is verified. This is
the same policy used by Atoll's package and Git APIs; see [Package security scanning](SECURITY.md).

Atoll does not currently implement aurweb JSONP callbacks or aurweb's per-IP request rate limit. Neither is required by
yay or paru.

## Quick verification

Check package metadata:

```bash
curl 'http://localhost:5290/rpc?v=5&type=info&arg[]=package-name'
```

Check the Git endpoint:

```bash
git ls-remote http://localhost:5290/package-base.git
```
