# 10. Deploying to Azure

A complete walkthrough: Keycloak running on Azure, configured by the same
Terraform that configures your local container, with your applications alongside
it.

> **Read this first.** The deployment costs roughly **$40–60/month** if left
> running, and everything lives in two resource groups that
> `make azure-destroy` removes. Nothing here touches existing resources. See
> [Cost and teardown](#cost-and-teardown) before you start.

You can stop after **Step 3** and have a fully working cloud Keycloak driving
your local applications, for about $40/month. Steps 4–6 deploy the applications
too.

---

## Prerequisites

```bash
# Terraform is no longer in homebrew-core, so the tap is required
brew tap hashicorp/tap && brew install hashicorp/tap/terraform
brew install azure-cli
# other platforms: https://developer.hashicorp.com/terraform/install

az login
az account set --subscription "<your subscription>"   # if you have several
az account show --query name -o tsv                   # confirm the right one
```

You need permission to create resource groups and assign roles (Owner, or
Contributor + User Access Administrator). The role assignments are what let the
apps read Key Vault and pull images without passwords.

## What you will build

```
rg-docvault-keycloak                       rg-docvault-lab
├── VNet 10.20.0.0/16                      ├── Container Registry
│   ├── snet-container-apps  /23           ├── Container App  (DocVault API)
│   │   └── Container App: Keycloak        ├── Static Web Apps x2  (React, Vue)
│   └── snet-postgres        /24           ├── Key Vault
│       └── Postgres 16  (no public IP)    ├── Log Analytics + App Insights
├── Private DNS zone                       └── Microsoft Sentinel
├── Key Vault  (admin + DB passwords)
├── Managed identity
└── Log Analytics
```

Steps 1–3 build the left column. Steps 4–6 build the right.

---

## Step 1 — Keycloak

```bash
make azure-plan      # read-only; creates nothing. Read it.
make azure-apply     # ~10-15 min, mostly Postgres. Prompts before creating.
```

`azure-apply` also writes `infra/environments/azure/realm.tfvars`, so no secret
is copied by hand.

**Verify** — the issuer must be the *public* HTTPS hostname:

```bash
ISS=$(terraform -chdir=infra/terraform/10-keycloak-azure output -raw issuer)
curl -s "$ISS/.well-known/openid-configuration" | jq -r .issuer
```

If that prints an internal hostname instead, `KC_HOSTNAME`/`KC_PROXY_HEADERS` are
wrong and every OIDC client will fail later.

## Step 2 — The realm

```bash
make seed-azure      # the SAME module that seeds your local Keycloak
```

**Verify** — 71 resources, and the two settings that fail silently when missing:

```bash
curl -s "$ISS/.well-known/openid-configuration" | jq -r '.acr_values_supported'
# ["bronze","silver","0","1","2"]   <- the ACR->LoA map applied
```

```bash
make azure-secrets                    # issuer + worker client secret
SECRET=$(cd infra/terraform/20-realm && terraform output -raw worker_client_secret)
curl -s -X POST "$ISS/protocol/openid-connect/token" \
  -d client_id=docvault-worker -d "client_secret=$SECRET" \
  -d grant_type=client_credentials | jq -r '.access_token' \
  | cut -d. -f2 | base64 -d 2>/dev/null | jq '{aud, resource_access}'
# aud must contain "docvault-api"   <- the audience mapper applied
```

**Admin console** — user `admin`, password from Key Vault:

```bash
terraform -chdir=infra/terraform/10-keycloak-azure output -raw admin_console_url

VAULT=$(az keyvault list -g rg-docvault-keycloak --query "[0].name" -o tsv)
az keyvault secret show --vault-name "$VAULT" \
  --name keycloak-bootstrap-admin-password --query value -o tsv
```

Switch from the `master` realm to **`docvault`** to see the demo users.

## Step 3 — Point your local apps at it

**This is the step worth doing even if you go no further.** The Azure realm
registers `localhost` redirect URIs, so your local apps work against cloud
Keycloak with no further deployment and no extra cost.

```bash
# terminal 1
Keycloak__Authority="$ISS" Keycloak__RequireHttpsMetadata=true \
ASPNETCORE_URLS=http://localhost:5001 \
  dotnet run --project apps/api-dotnet/DocVault.Api --no-launch-profile

# terminal 2
cd apps/web-react && VITE_OIDC_AUTHORITY="$ISS" npm run dev
```

Sign in at <http://localhost:5173> as `alice` / `DocVaultLab!2026`. The login page
is now served by Azure.

**Verify** — the whole browser suite passes against the cloud:

```bash
cd tests/e2e-playwright && npx playwright test
```

> `RequireHttpsMetadata=true` is required. The API refuses to start with a
> non-loopback authority over plain HTTP — fetching signing keys unencrypted from
> a remote host would let an attacker substitute their own.

---

## Step 4 — The container registry and API image

The container app references an image that must already exist, so the registry is
created first.

```bash
cd infra/terraform/30-azure
terraform init
terraform apply -target=azurerm_container_registry.acr
```

`-target` is normally a smell; a registry that must be populated before anything
references it is the legitimate exception, and you do it once.

```bash
ACR=$(terraform output -raw acr_name)
az acr build --registry "$ACR" --image docvault-api:1.0.0 ../../../apps/api-dotnet
```

`az acr build` builds **in Azure** — no local Docker daemon, and no cross-building
`linux/amd64` from an Apple Silicon Mac.

## Step 5 — The applications

```bash
terraform apply \
  -var="keycloak_issuer=$(terraform -chdir=../10-keycloak-azure output -raw issuer)" \
  -var="container_image=$(terraform output -raw acr_login_server)/docvault-api:1.0.0"
```

**Verify:**

```bash
API=$(terraform output -raw api_url)
curl -s "$API/health"                              # {"status":"ok"}
curl -s -o /dev/null -w '%{http_code}\n' "$API/documents"   # 401 - deny by default
```

## Step 6 — Tell Keycloak about the deployed URLs

The realm still trusts `localhost`. Point it at the real hostnames:

```bash
terraform -chdir=infra/terraform/30-azure output api_url react_url vue_url
```

Put those into `infra/environments/azure/realm.tfvars` as `api_origin`,
`web_react_origin` and `web_vue_origin`, then:

```bash
make seed-azure
```

> **Static Web Apps are created empty.** Terraform provisions them; it does not
> upload content. Build the SPAs with `VITE_OIDC_AUTHORITY` set to your issuer and
> deploy them with the [SWA CLI](https://azure.github.io/static-web-apps-cli/) or
> GitHub Actions. Until then, use Step 3's local front end against the deployed API.

---

## Cost and teardown

| Item | Approx. /month |
|---|---|
| Postgres B1ms + 32 GB | ~$19 |
| Keycloak container app (1 vCPU / 2 GiB, always on) | ~$22 |
| API container app (0.5 vCPU / 1 GiB) | ~$11 |
| Key Vault, VNet, DNS, identities | ~$1 |
| Static Web Apps ×2 | Free tier |
| **Microsoft Sentinel** | **per GB ingested** — free for 31 days on a new workspace |

Roughly **$40/month** after Steps 1–3, **$55–60** after Step 5. About **$1.30–2/day**,
so a few days of experimenting is small — but Sentinel is the least predictable
line, and it is only there for [uc6](use-cases/uc6-audit-siem-sentinel.md).

```bash
make azure-destroy                              # Keycloak (rg-docvault-keycloak)
terraform -chdir=infra/terraform/30-azure destroy   # the apps (rg-docvault-lab)
```

Both remove their whole resource group. The Key Vaults are created with
`purge_soft_delete_on_destroy`, so the names are released rather than left
reserved for 7 days.

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| Issuer is an internal hostname | `KC_HOSTNAME` / `KC_PROXY_HEADERS` not applied | Both are set by the module; check the revision picked up the env vars |
| Revision fails, `UNAUTHORIZED` pulling the image | The AcrPull grant did not exist when the revision started | `terraform apply` again — `depends_on` normally orders this |
| `terraform apply` wants to recreate the local realm | Wrong workspace | The make targets select `local`/`azure`; by hand, `terraform workspace select azure` |
| HA fails at plan | Zone-redundant HA is unavailable on Burstable | Use a `GP_` SKU, or leave `postgres_zone_redundant = false` |
| API 401s every request | Wrong `Keycloak__Authority`, or the audience mapper is missing | Compare `iss` in a real token against the API's configured authority |

More in [11. Troubleshooting](11-troubleshooting.md).

---

## How it works

### Why Container Apps is viable now

Historically clustered Keycloak on Azure PaaS was painful: Infinispan discovered
peers over **UDP multicast**, which Azure does not provide, so you effectively
needed AKS with `KUBE_PING`.

**Keycloak 26.1 changed the default cache stack to `jdbc-ping`** — nodes find each
other through a table in the database instead of the network. This repo pins
26.6.3, so multi-replica works on Container Apps with no special networking.

The consequence worth internalising: **the database is now a hard dependency for
clustering, not just for storage.** If Postgres is unavailable the cluster cannot
form, not merely "sessions are slow".

### How images are pulled

No registry password exists anywhere. The container app authenticates as its
user-assigned identity, which holds **AcrPull**:

```hcl
registry {
  server   = azurerm_container_registry.acr.login_server
  identity = azurerm_user_assigned_identity.api.id
}
```

`admin_enabled = false` on the registry, deliberately — the admin user is a shared
username/password that cannot be scoped or attributed to anyone.

### Settings that are load-bearing

Each produces a confusing failure when wrong rather than an obvious one.

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
attribute of the *environment*, created first, so the module derives the FQDN in a
`local` and applies in one pass.

**`start`, never `start-dev`.** Dev mode disables hostname and HTTPS checks and
will run quite happily in production while silently weakening both.

**`sslmode=require` in the JDBC URL.** Azure Postgres rejects unencrypted
connections and the failure reads like a generic connection error.

**Restricted password alphabet.** `@ : / ? &` terminate or escape a JDBC URL, and
the result surfaces as an *authentication* error — sending you to look in entirely
the wrong place.

**`min_replicas` is validated to be ≥ 1.** Keycloak cold-starts slowly and holds
authentication sessions in memory; scaling to zero logs everyone out and makes the
next login wait for a JVM boot.

**No `0.0.0.0` firewall rule.** The common shortcut is public Postgres plus the
"allow all Azure services" rule — that rule admits *every* Azure tenant, not just
yours. A VNet with two delegated subnets removes the internet from the picture.

**HA is unavailable on Burstable.** `postgres_zone_redundant = true` with the
default `B_Standard_B1ms` fails at apply. A `precondition` catches it at plan time
and tells you to move to a `GP_`/`MO_` SKU.

## Production hardening

A working starting point, not a finished production deployment. Before real traffic:

- [ ] **Build an optimized Keycloak image.** The default runs `kc.sh start`, which
      performs the build step on every cold start (~30–60s) *at runtime*, where it
      can fail in a live environment. Use the included `Dockerfile`:
      `az acr build --registry <acr> --image keycloak:26.6.3 .`, then set
      `-var="keycloak_image=..."` — the module switches to `start --optimized`.
- [ ] **A custom domain**, decided *before* onboarding any application. The
      hostname becomes the token issuer, and changing an issuer invalidates every
      token and breaks every client.
- [ ] **Delete the bootstrap admin.** `KC_BOOTSTRAP_ADMIN_*` seeds a temporary
      master-realm admin on an empty database. Create a service account for
      `20-realm`, then remove the password admin — a permanent one is a standard
      way self-hosted Keycloak gets compromised.
- [ ] `prevent_destroy` on the database (omitted so the lab can be torn down; in
      production the DB holds the one thing Terraform cannot recreate).
- [ ] Zone-redundant HA on a GP SKU, and **restore-test** a backup.
- [ ] Key Vault behind a private endpoint, with Terraform running inside the VNet.
- [ ] WAF via Front Door or Application Gateway.
- [ ] Remote Terraform state. The modules use local state, which is fine for one
      person and wrong for a team.
- [ ] An upgrade process. You own CVE response for Keycloak, the JVM and the base image.

## What running it actually costs

Standing this up is unremarkable — one `terraform apply`. *Running* it is the real
cost:

- **Upgrades**, roughly quarterly, occasionally breaking.
- **CVE response** for Keycloak, the JVM and the base image.
- **Backups you have restored**, not merely configured.
- **HA across zones**, which means a GP-tier database.
- **Being on call.** If Keycloak is down nobody can log in to anything — an
  authentication outage is a total outage.

None of that is a reason not to self-host. It is a reason to decide deliberately,
and to compare against engineer-hours rather than licence fees.

## The point this proves

```bash
make seed         # local Docker Keycloak
make seed-azure   # Keycloak on Azure
```

Same module. Same 71 resources. Same realm. Only a URL and a credential differ —
which is why the free local lab is a faithful rehearsal for the cloud one rather
than a simplified toy.
