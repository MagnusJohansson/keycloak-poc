# UC6 — Audit logging into Azure Sentinel

> The second genuinely Azure-flavoured part: Keycloak security events land beside
> your application telemetry, where they can be correlated and alerted on.

## Problem

"Who granted that role at 2am?" and "was that a credential-stuffing attempt or a
user who forgot their password?" are questions you can only answer if the events
were being recorded *before* you needed them.

Keycloak records two independent streams, and **both are off by default** — a
common and expensive surprise during an incident review.

## Enable them

```hcl
resource "keycloak_realm_events" "docvault" {
  events_enabled    = true
  events_expiration = 604800     # 7 days in the DB; ship elsewhere for real retention

  admin_events_enabled         = true
  admin_events_details_enabled = true

  enabled_event_types = ["LOGIN", "LOGIN_ERROR", "LOGOUT", "REFRESH_TOKEN_ERROR",
                         "CLIENT_LOGIN_ERROR", "GRANT_CONSENT", "REVOKE_GRANT",
                         "UPDATE_TOTP", "REMOVE_TOTP", /* … */]
  events_listeners = ["jboss-logging"]
}
```

| Stream | Records | Answers |
|---|---|---|
| **User events** | logins, failures, consents, token refreshes | "was this account attacked?" |
| **Admin events** | configuration changes, with before/after detail | "who changed this, and when?" |

`events_expiration` is database retention, not archival. Seven days is enough to
debug; compliance retention belongs in the SIEM.

> Enabling `admin_events_details_enabled` records full request payloads. Useful,
> but it means the event log now contains configuration detail — treat it as
> sensitive and restrict who can read it.

## Ship to Sentinel

Self-hosting means you build this yourself — there is no managed export. The
good news is that the Azure deployment in
[09-deploying-on-azure](../09-deploying-on-azure.md) already gives you the whole
path for free, because Container Apps streams container stdout into Log Analytics:

```
Keycloak  --jboss-logging listener-->  stdout
          --Container Apps-->  Log Analytics (ContainerAppConsoleLogs_CL)
          --onboarding-->  Microsoft Sentinel
```

`events_listeners = ["jboss-logging"]` (set in `20-realm/events.tf`) is what
writes each event to the server log. `10-keycloak-azure` attaches a Log Analytics
workspace to the Container Apps environment, and `30-azure` onboards a workspace
to Sentinel:

```hcl
resource "azurerm_sentinel_log_analytics_workspace_onboarding" "lab" {
  count        = var.enable_sentinel ? 1 : 0
  workspace_id = azurerm_log_analytics_workspace.lab.id
}
```

> **Sentinel is off by default** — it bills per GB analysed, so a lab you clone
> should not start metered billing unasked. Enable it with
> `-var="enable_sentinel=true"`.
>
> The collection half works regardless: Container Apps streams Keycloak's stdout
> into Log Analytics, so you can run every KQL query below against the workspace
> without Sentinel. What Sentinel adds is the SIEM layer on top — analytics
> rules, incidents and hunting.

So no extra component is required to get events into Sentinel — only a parser,
since they arrive as log lines rather than structured rows.

> Point both modules at the **same** workspace if you want Keycloak events and
> application telemetry correlatable. Two workspaces means cross-workspace
> queries and a worse time during an incident.

### Structured output instead of log scraping

Log lines are workable but lossy. Two better options, in increasing order of effort:

1. **Raise the log format to JSON** — `KC_LOG_CONSOLE_OUTPUT=json`. Events arrive
   as parseable JSON in one field instead of a formatted string.
2. **Write an event listener SPI** that POSTs events to an HTTP endpoint (an Azure
   Function, Event Hub, or the Log Analytics ingestion API). This is the
   production answer: it gives you real fields, delivery you control, and no
   dependency on log formatting that changes between Keycloak releases.

Keycloak's SPI mechanism is the extension point — implement
`EventListenerProviderFactory` and drop the JAR into `/opt/keycloak/providers`.

## Queries worth having

Credential stuffing — many failures, many accounts, one IP:

```kusto
KeycloakEvents_CL
| where type_s == "LOGIN_ERROR"
| summarize failures = count(), users = dcount(userId_s) by ipAddress_s, bin(TimeGenerated, 5m)
| where failures > 20 and users > 5
```

Privilege escalation outside change windows:

```kusto
KeycloakAdminEvents_CL
| where operationType_s in ("CREATE", "UPDATE")
  and resourceType_s in ("REALM_ROLE_MAPPING", "CLIENT_ROLE_MAPPING")
| where dayofweek(TimeGenerated) in (0d, 6d) or hourofday(TimeGenerated) !between (7 .. 19)
| project TimeGenerated, authDetails_userId_s, resourcePath_s, representation_s
```

Impossible travel:

```kusto
KeycloakEvents_CL
| where type_s == "LOGIN"
| summarize locations = dcount(ipAddress_s), ips = make_set(ipAddress_s) by userId_s, bin(TimeGenerated, 1h)
| where locations > 1
```

The column names depend on how your connector shapes the payload — check the
actual schema before relying on these.

## Locally

Without a SIEM, events are visible in the admin console under **Realm settings →
Sessions → Events**, and via the Admin REST API — which is also a perfectly
reasonable low-tech shipping mechanism if you would rather poll than write an SPI:

```bash
TOKEN=$(curl -s -X POST http://localhost:8080/realms/master/protocol/openid-connect/token \
  -d client_id=admin-cli -d username=admin -d password=admin -d grant_type=password \
  | python3 -c 'import json,sys; print(json.load(sys.stdin)["access_token"])')

curl -s -H "Authorization: Bearer $TOKEN" \
  'http://localhost:8080/admin/realms/docvault/events?max=20' | python3 -m json.tool
```

Sign in and out a few times first, then look for `LOGIN`, `LOGIN_ERROR` and
`CODE_TO_TOKEN`.

## Alerting is the point

Collecting logs nobody reads is theatre. At minimum, alert on:

- a spike in `LOGIN_ERROR` for one account or from one IP
- **any** admin event touching role mappings or identity providers
- `REMOVE_TOTP` — an attacker's first move after taking an account is often
  removing the second factor
- `CLIENT_LOGIN_ERROR` — a service credential that has expired or been revoked
