# 12. Troubleshooting

Nearly every problem below shares a cause: **something is configured that produces
no error when missing.** Start with the token.

```bash
make token          # decode a real token: aud, resource_access, exp, acr
```

## 401 Unauthorized

| Symptom | Cause | Fix |
|---|---|---|
| `The audience 'docvault-web-react' is invalid` | Missing audience mapper — the token's `aud` is the *calling* client | Check `keycloak_openid_audience_protocol_mapper` in `mappers.tf`; verify with `make token` |
| `IDX10205: Issuer validation failed` | `Authority` does not match `iss` exactly | Trailing slashes and `localhost` vs `127.0.0.1` both count as different |
| `IDX10223: Lifetime validation failed` | Expired token, or clock drift | `ClockSkew` is 30s here; check container time |
| 401 on **every** request, including valid ones | `Authority` unreachable, so JWKS never loads | `curl $AUTHORITY/.well-known/openid-configuration` |
| `The MetadataAddress or Authority must use HTTPS` | Not in Development, `RequireHttpsMetadata` defaulted to true | Set `Keycloak:RequireHttpsMetadata=false` — only valid for a loopback authority |

## 403 Forbidden

| Symptom | Cause | Fix |
|---|---|---|
| 403 with a valid token and the right role | **Claims transformation missing or not registered** | Roles are nested in `realm_access` / `resource_access`; ASP.NET Core needs them flattened |
| Role visible in the admin console, absent from the token | Role assigned to the user but scoped to another client | Only *this* client's roles are imported, deliberately |
| 403 on `/documents` for a valid user | No tenant — user is in no group | Add a group membership; see `dave` |
| 403 that a re-login does not fix | Working as intended | 403 means "not permitted", not "not authenticated" |
| Service account 403s | Holds a role but no tenant group | See [uc4](use-cases/uc4-service-to-service.md) — a role is not an authorization |

## Login and redirect

| Symptom | Cause | Fix |
|---|---|---|
| `Invalid parameter: redirect_uri` | URI not registered, or the port shifted | Both SPAs use `strictPort`; check `valid_redirect_uris` |
| CORS error on the token call | `web_origins` not set on the client | Set it — separate from redirect URIs |
| Mobile sign-in never returns | Custom scheme not registered natively | Android `manifestPlaceholders`, iOS `CFBundleURLTypes` |
| Mobile cannot reach Keycloak | `localhost` inside an emulator is the emulator | Android emulator: `10.0.2.2` |
| Silent renew fails, user logged out | Third-party cookies blocked | Custom domain sharing a site with the app, or a BFF |
| Step-up challenge invisible to the SPA; user sees a bare 403 | `WWW-Authenticate` is **not** CORS-safelisted, so a cross-origin SPA cannot read it | Add `.WithExposedHeaders("WWW-Authenticate")` to the CORS policy |

## Step-up

| Symptom | Cause | Fix |
|---|---|---|
| `acr_values` ignored, `acr` never changes | **`acr.loa.map` not set on the realm** | The single most common step-up failure; check `acr_values_supported` in discovery |
| User is not prompted, returns immediately | Missing `prompt=login` — the SSO cookie satisfied it | Add it to the step-up request |
| Step-up works once, then stops prompting | `loa-max-age` too long | 300s for LoA 2 here; a long-lived step-up steps up nothing |
| Authentication broken after applying the flow | Executions created out of order | Terraform parallelises; `authn-stepup.tf` needs `depends_on` chains |

## Terraform

| Symptom | Cause | Fix |
|---|---|---|
| 500 creating the API client | `BEARER-ONLY` + authorization services | Must be `CONFIDENTIAL` with `service_accounts_enabled` |
| `client with id docvault-analytics does not exist` | Passed the OAuth `client_id` string | `*_scopes` resources want the internal UUID (`.id`) |
| `invalidPasswordMinLengthMessage` seeding users | Seed password violates the realm's own policy | Realm requires length 12 |
| `Failed to get redirect URIs from the Sector Identifier URI` | Keycloak fetches that URL at mapper-creation time | Omit `sectorIdentifierUri` for a single-host client |
| 409 Conflict on users after a failed apply | Partial apply left objects Terraform does not know about | Delete the realm and re-apply; state and reality must agree |
| Realm changes silently reverted | Someone edited the admin console | `make plan` should be empty; treat drift as a bug |

## Local lab

| Symptom | Cause | Fix |
|---|---|---|
| `make up` fails pulling images | Docker Hub anonymous rate limit (429) | Defaults avoid Docker Hub; override with `POSTGRES_IMAGE=…` |
| Keycloak unhealthy but reachable | Health endpoint is on port **9000**, not 8080 | By design in Keycloak 25+ |
| Realm gone after `make clean` | `clean` removes the volume | Use `make down` to keep data |
| Changes to the realm JSON ignored | It is a **generated artifact** | Edit the HCL; `make export-realm` regenerates |

## Reading the logs

```bash
make logs                                              # Keycloak
docker compose -f infra/local/docker-compose.yml logs -f keycloak | grep -i error
```

The API logs every validation failure with its reason
(`JwtBearerEvents.OnAuthenticationFailed`) — that message names the exact
validation that failed and is usually faster than any guesswork.

## Still stuck

1. `make token` — is the claim you expect actually in the token?
2. Compare the token against `/me` — do the browser and the API disagree?
3. `curl $AUTHORITY/.well-known/openid-configuration` — is the issuer reachable
   and is the string identical to the API's `Authority`?
4. `make plan` — has the realm drifted from the code?

[Keycloak documentation](https://www.keycloak.org/documentation) · [Keycloak server admin guide](https://www.keycloak.org/docs/latest/server_admin/)
