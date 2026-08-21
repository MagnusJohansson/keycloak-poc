# 10. Deploying Keycloak on Azure

> Source: `infra/terraform/10-keycloak-azure` (Keycloak itself) and
> `infra/terraform/30-azure` (your applications).

## Architecture

```
VNet 10.20.0.0/16
├── snet-container-apps  10.20.0.0/23   delegated to Microsoft.App/environments
│   ├── Keycloak            (1-3 replicas, Container Apps)
│   └── DocVault API        (Container Apps)
└── snet-postgres        10.20.2.0/24   delegated to DBforPostgreSQL/flexibleServers
    └── Postgres Flexible Server 16     NO public endpoint

Key Vault ── secrets read via managed identity (no credentials in config)
Log Analytics + Sentinel ── Keycloak events and app telemetry
Entra ID ── optionally federated into Keycloak as an upstream IdP (uc2)
Static Web Apps ── React and Vue clients
```

Two Terraform modules, applied in order: `10-keycloak-azure` stands up Keycloak,
then `20-realm` configures it, then `30-azure` deploys your applications against it.

## What carries over from the local lab

Everything except the hosting. `20-realm` — all 68 resources — applies unchanged;
it talks to the Keycloak Admin REST API, which is identical in both places.

| Component | Change needed |
|---|---|
| `20-realm` — the whole realm | **none**, different `keycloak_url` |
| API, React, Vue, Flutter, Electron | **none** — only the `Authority` / issuer URL |
| `infra/local/docker-compose.yml` | not used in Azure |

## Deploy

```bash
az login
make azure-plan      # read-only; creates nothing
make azure-apply     # ~10-15 min, mostly Postgres
make seed-azure      # the SAME realm module you ran against Docker
make apps-plan       # then deploy your applications (30-azure)
```

`azure-apply` writes `infra/environments/azure/realm.tfvars` for you,
so no secret is transcribed by hand. Retrieve the bootstrap admin password with:

```bash
az keyvault secret show --vault-name <vault> \
  --name keycloak-bootstrap-admin-password --query value -o tsv
```

## What gets created (20 resources)

```
VNet 10.20.0.0/16
├── snet-container-apps  10.20.0.0/23   delegated to Microsoft.App/environments
│   └── Container Apps environment → Keycloak (1-3 replicas)
└── snet-postgres        10.20.2.0/24   delegated to DBforPostgreSQL/flexibleServers
    └── Postgres Flexible Server 16     NO public endpoint
private DNS zone ─ resolves the DB's FQDN inside the VNet
Key Vault ─ DB + bootstrap admin passwords, read via managed identity
Log Analytics
```

## Why Container Apps is viable now

Historically clustered Keycloak on Azure PaaS was painful: Infinispan discovered
peers over **UDP multicast**, which Azure does not provide, so you effectively
needed AKS with `KUBE_PING`.

**Keycloak 26.1 changed the default cache stack to `jdbc-ping`** — nodes find
each other through a table in the database instead of the network. This repo
pins 26.6.3, so it applies, and multi-replica works on Container Apps with no
special networking.

The consequence worth internalising: **the database is now a hard dependency for
clustering, not just for storage.** If Postgres is unavailable the cluster
cannot form, not merely "sessions are slow".

## Settings that are load-bearing

Each of these produces a confusing failure when wrong rather than an obvious one.

**Hostname and proxy.** Container Apps terminates TLS at the edge and forwards
plain HTTP:

```hcl
KC_HOSTNAME      = "https://<fqdn>"   # the PUBLIC url
KC_HTTP_ENABLED  = "true"             # accept HTTP on the container port
KC_PROXY_HEADERS = "xforwarded"       # trust X-Forwarded-* to build absolute URLs
```

Omit the last two and Keycloak builds redirect and issuer URLs from the internal
hostname. Every OIDC client then fails with "invalid redirect_uri" or an issuer
mismatch that is genuinely unpleasant to trace.

**The FQDN chicken-and-egg is solved.** `KC_HOSTNAME` must be the public URL, but
the app's FQDN normally only exists after the app does. `default_domain` is an
attribute of the *environment*, which is created first, so the module derives the
FQDN in a `local` and applies in one pass.

**`start`, never `start-dev`.** Dev mode disables hostname and HTTPS checks and
will run quite happily in production while silently weakening both.

**`sslmode=require` in the JDBC URL.** Azure Postgres rejects unencrypted
connections and the failure reads like a generic connection error.

**Restricted password alphabet.** `@ : / ? &` terminate or escape a JDBC URL, and
the result surfaces as an *authentication* error — sending you to look in
entirely the wrong place.

**`min_replicas` is validated to be ≥ 1.** Keycloak cold-starts slowly and holds
authentication sessions in memory; scaling to zero logs everyone out and makes
the next login wait for a JVM boot.

**No `0.0.0.0` firewall rule.** The common shortcut is public Postgres plus the
"allow all Azure services" rule — that rule admits *every* Azure tenant, not just
yours. A VNet with two delegated subnets is a few more lines and removes the
internet from the picture.

**HA is unavailable on Burstable.** `postgres_zone_redundant = true` with the
default `B_Standard_B1ms` fails at apply. A `precondition` catches it at plan
time and tells you to move to a `GP_`/`MO_` SKU.

## Production hardening

The module is a working starting point, not a finished production deployment.
Before real traffic:

- [ ] **Build an optimized image.** The default runs `kc.sh start`, which
      performs the build step on every cold start (~30–60s) *at runtime*, where
      it can fail in a live environment. Use the included `Dockerfile`:
      `az acr build --registry <acr> --image keycloak:26.6.3 .`, then set
      `-var="keycloak_image=..."` — the module switches to `start --optimized`.
- [ ] **Custom domain**, decided *before* onboarding any application. The
      hostname becomes the token issuer, and changing an issuer invalidates every
      token and breaks every client.
- [ ] **Delete the bootstrap admin.** `KC_BOOTSTRAP_ADMIN_*` seeds a temporary
      master-realm admin on an empty database. Create a service account for
      `20-realm`, then remove the password admin — a permanent one is a standard
      way self-hosted Keycloak gets compromised.
- [ ] `prevent_destroy` on the database (omitted here so the lab can be torn
      down; in production the DB holds the one thing Terraform cannot recreate).
- [ ] Zone-redundant HA on a GP SKU, and **restore-test** a backup.
- [ ] Key Vault behind a private endpoint, with Terraform running inside the VNet.
- [ ] WAF via Front Door or Application Gateway.
- [ ] Ship Keycloak events to Sentinel — Container Apps already streams stdout to
      Log Analytics, so this is mostly a parser. See [uc6](use-cases/uc6-audit-siem-sentinel.md).
- [ ] An upgrade process. You now own CVE response for Keycloak, the JVM and the
      base image.

## What running it actually costs

Standing this up is unremarkable — one `terraform apply`. *Running* it is the
real cost, and it is worth being clear-eyed about before committing:

- **Upgrades**, roughly quarterly, occasionally breaking.
- **CVE response** for Keycloak, the JVM and the base image.
- **Backups you have restored**, not merely configured.
- **HA across zones**, which means a GP-tier database.
- **Being on call.** If Keycloak is down nobody can log in to anything — an
  authentication outage is a total outage.

None of that is a reason not to self-host. It is a reason to decide deliberately,
and to compare against engineer-hours rather than licence fees. If the ops burden
turns out not to be worth it, managed Keycloak vendors and Red Hat build of
Keycloak exist, and the realm export in this repo is your migration path either
way.

## The point this proves

Run both and compare:

```bash
make seed         # local Docker Keycloak
make seed-azure   # Keycloak on Azure
```

Same module. Same 68 resources. Same realm. Only a URL and a credential differ.
That is what portability looks like when it is real rather than asserted — and it
is why the free local lab is a faithful rehearsal for the cloud one rather than a
simplified toy.
