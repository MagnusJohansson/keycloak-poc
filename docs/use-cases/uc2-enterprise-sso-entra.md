# UC2 — Enterprise SSO with Microsoft Entra ID

> The genuinely Azure-flavoured part of this lab. Staff sign in with their
> corporate account; Keycloak brokers the identity.

## Problem

Acme's staff already exist in Entra ID. They should not get a second password,
and when someone leaves, disabling the Entra account must remove DocVault access
immediately.

## Shape

```
user ──▶ DocVault ──▶ Keycloak (docvault realm) ──▶ Entra ID
                          │                            │
                          │◀── OIDC id_token ──────────┘
                          │
                          └── maps claims -> groups + roles, issues ITS OWN token
```

Keycloak is an **identity broker**. Your applications only ever trust one issuer,
even with many upstream providers. Adding Google tomorrow changes nothing in the
API or the SPAs.

## Configure

**1. Register the app in Entra ID**

- Redirect URI: `https://<your-keycloak-host>/realms/docvault/broker/entra/endpoint`
- Note the tenant ID, client ID, and a client secret.

**2. Add the provider**

```hcl
resource "keycloak_oidc_identity_provider" "entra" {
  realm             = keycloak_realm.docvault.id
  alias             = "entra"          # must match the redirect URI above
  display_name      = "Sign in with Microsoft"

  authorization_url = "https://login.microsoftonline.com/${var.entra_tenant_id}/oauth2/v2.0/authorize"
  token_url         = "https://login.microsoftonline.com/${var.entra_tenant_id}/oauth2/v2.0/token"
  client_id         = var.entra_client_id
  client_secret     = var.entra_client_secret
  default_scopes    = "openid profile email"

  trust_email       = true    # Entra verifies email; do NOT set this for social IdPs
  sync_mode         = "FORCE" # re-apply mappers on every login, so changes take effect
}
```

Two settings deserve care:

- **`trust_email = true`** skips Keycloak's own verification. Safe for a corporate
  IdP you control. Dangerous for a social provider, where an attacker could
  register an account claiming a victim's address and get it auto-linked.
- **`sync_mode = "FORCE"`** re-runs mappers on every login. With the default
  (`IMPORT`) attributes are set once at first login, so a later group change in
  Entra never reaches Keycloak — a stale-permissions bug that is unpleasant to
  diagnose.

**3. Map Entra groups onto DocVault groups**

```hcl
resource "keycloak_attribute_to_role_identity_provider_mapper" "acme_engineering" {
  realm                   = keycloak_realm.docvault.id
  identity_provider_alias = keycloak_oidc_identity_provider.entra.alias
  attribute_name          = "groups"
  attribute_value         = var.entra_engineering_group_object_id  # an OID, not a name
  role                    = "docvault-api.doc.editor"
}
```

Entra emits group **object IDs**, not display names, unless you configure group
claims to emit names. Match on the OID.

> Entra caps the `groups` claim (~150 for id_tokens) and switches to a Graph API
> "overage" pointer beyond that. For large directories, emit **app roles** instead
> of groups — they are scoped to the application and do not overflow.

## Alternative: SCIM

Group mapping happens at *login*. SCIM provisions users continuously, so accounts
are created and **deactivated** without waiting for a sign-in attempt. For
offboarding, that difference matters: broking alone leaves a disabled user's
Keycloak session valid until it expires.

Use both — SCIM for lifecycle, brokering for authentication.

## Verify

1. Sign in; the Keycloak login page offers **Sign in with Microsoft**.
2. Complete Entra login, including its MFA.
3. Check the TokenInspector: `resource_access.docvault-api.roles` should reflect
   the mapped Entra group.
4. In Keycloak, **Users → the user → Identity provider links** shows the federated link.

## Trade-off

Brokering adds a hop. If *every* user is in Entra and you need no other provider,
point the apps at Entra directly and skip Keycloak. Brokering earns its place when
you have several IdPs, or external users alongside staff, or want authorization
modelled independently of the corporate directory.
