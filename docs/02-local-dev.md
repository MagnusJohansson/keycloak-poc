# 3. Local development

The whole lab runs on your machine, free, against upstream Keycloak.

```bash
make up      # Keycloak 26.6.3 + Postgres + Mailpit
make seed    # apply the DocVault realm
```

| Service | URL | Credentials |
|---|---|---|
| Keycloak admin | http://localhost:8080 | `admin` / `admin` |
| Mailpit (catches email) | http://localhost:8025 | — |
| API | http://localhost:5001 | bearer token |
| React SPA | http://localhost:5173 | `alice` / `DocVaultLab!2026` |
| Vue SPA | http://localhost:5174 | same |

Demo users, each chosen to make one authorization outcome visible:

| User | Groups | Roles | Demonstrates |
|---|---|---|---|
| `alice` | `/acme/engineering` | `doc.reader`, `doc.editor` | the happy path |
| `bob` | `/globex` | `doc.reader` | tenant isolation — cannot see Acme documents |
| `carol` | `/acme/legal` | `doc.admin`, `platform-admin` | needs OTP step-up for classified |
| `dave` | *(none)* | *(none)* | authenticated but unauthorized → **403, not 401** |

## Why this is worth having

Beyond costing nothing, it makes the central claim testable. `20-realm` applies
to this container and to a managed cluster with no change but a URL. If that ever
stops being true, `make seed` fails and you find out immediately rather than
during a migration.

It also means CI needs no cloud account and no secrets — the entire test suite
runs against Docker.

## Image sources

Docker Hub rate-limits anonymous pulls (HTTP 429), and that is the most common
reason `make up` fails on a fresh machine. The defaults therefore avoid it:
Keycloak from `quay.io` (its home registry), Postgres and Mailpit through
Google's public pull-through mirror. Override if you prefer:

```bash
POSTGRES_IMAGE=docker.io/library/postgres:17-alpine make up
```

## Keeping parity with the cloud

- **Pin the same Keycloak version.** `infra/local/docker-compose.yml` pins
  `26.6.3`; `10-platform` pins `26.6`. Drift between them is how "works locally"
  becomes "breaks in staging".
- **Match the feature flags.** The compose file enables `organization` and
  `opentelemetry` to match the managed offering.
- **Do not rely on `start-dev` behaviour.** It relaxes hostname and HTTPS checks.
  Anything that only works because of that will fail in Azure, where Keycloak
  runs with `KC_HOSTNAME` set and `ssl_required = "all"`.

## Terraform is the source of truth

`infra/local/realm-export/docvault-realm.json` is a **generated artifact**,
produced by `make export-realm` and committed only for fast seeding. Edit the
HCL, re-run `make seed`, then regenerate. Never hand-edit the JSON — the next
`make seed` will silently overwrite your changes.

Note that the export deliberately contains **no client secrets and no user
passwords**; Keycloak regenerates them on import.

## Useful commands

```bash
make token          # mint a client_credentials token and decode its claims
make plan           # what would applying the realm change?
make logs           # tail Keycloak
make clean          # tear everything down, including the database volume
```

`make token` is the fastest way to answer "why is the API rejecting me?" — check
`aud`, `resource_access` and `exp` before touching application code.

---

Next: [4. Realm design](03-realm-design.md).
