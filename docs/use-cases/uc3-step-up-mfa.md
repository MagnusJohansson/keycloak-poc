# UC3 — Step-up authentication

> Reading a document needs a password. Opening a **classified** document also
> needs a fresh one-time code — even if you are already signed in.

## Problem

Requiring MFA on every login trains people to click through it. Requiring it at
the moment of a sensitive action makes it meaningful, and keeps the everyday path
fast.

## The chain

```
1. GET /documents/classified          token has acr=bronze
2. API: 403 + WWW-Authenticate: Bearer error="insufficient_user_authentication",
                                acr_values="silver"
3. SPA: signinRedirect({ acr_values: 'silver', prompt: 'login' })
4. Keycloak: acr.loa.map -> silver = LoA 2 -> conditional OTP subflow -> prompt
5. New token: acr=silver
6. Retry -> 200
```

Every link is configuration, and each one fails silently if missing.

### The ACR → LoA map

```hcl
attributes = {
  "acr.loa.map" = jsonencode({ bronze = 1, silver = 2 })
}
```

**Without this, `acr_values` is ignored with no error.** The user is returned with
the same low ACR, the API refuses again, and the SPA loops. Confirm it took:

```bash
curl -s http://localhost:8080/realms/docvault/.well-known/openid-configuration \
  | python3 -c 'import json,sys; print(json.load(sys.stdin)["acr_values_supported"])'
# ['bronze', 'silver', '0', '1', '2']
```

### The flow

```
browser-stepup
├── auth-cookie                        ALTERNATIVE   already signed in? done (LoA 1)
└── stepup-forms                       ALTERNATIVE
    ├── stepup-loa1-password           CONDITIONAL   condition: LoA >= 1
    │   └── auth-username-password-form
    └── stepup-loa2-otp                CONDITIONAL   condition: LoA >= 2
        └── auth-otp-form
```

`loa-max-age` differs deliberately between the two:

```hcl
loa1: loa-max-age = "36000"   # 10h - the password satisfies the session
loa2: loa-max-age = "300"     #  5m - a step-up must be FRESH
```

A long-lived LoA 2 defeats the purpose: it would mean "you did MFA once this
morning, so you may open anything all day".

> **Ordering matters.** Keycloak assigns execution priority by creation sequence,
> and Terraform parallelises. `authn-stepup.tf` uses explicit `depends_on` chains;
> without them the flow comes out shuffled and authentication breaks confusingly.

### The API side

```csharp
.AddPolicy(Policies.Classified, p => {
    p.RequireRole("doc.admin");                            // permission
    p.AddRequirements(new StepUpAcrRequirement("silver")); // presence
});
```

Both are required, and they are independent. MFA does not substitute for a role
(`Stepping_up_does_not_substitute_for_the_role`), and a role does not substitute
for MFA.

The handler leaves the requirement **unmet** rather than calling `context.Fail()`,
so the result handler can tell "needs to step up" from "will never be allowed" and
emit an actionable challenge (RFC 9470) instead of a bare 403.

### The client side

```ts
auth.signinRedirect({ extraQueryParams: { acr_values: requiredAcr }, prompt: 'login' });
```

`prompt: 'login'` is essential. Without it the existing SSO cookie satisfies the
request silently, the user is never challenged, and you have a step-up that steps
up nothing.

## Try it

```bash
make up && make seed && make api && make web
```

1. Sign in as **carol** (`/acme/legal`, `doc.admin`).
2. Go to **Classified** → **Load classified documents** → the step-up notice appears.
3. First time only: Keycloak asks you to enrol an authenticator (scan the QR).
4. Enter the code → `acr: silver` → documents load.

Watch `acr` change in the TokenInspector.

Sign in as **alice** instead: she has `doc.editor`, not `doc.admin`, so she gets a
plain 403 with **no** step-up offer — correctly, because re-authenticating would
not help her.

Covered by `StepUpTests` (4 tests).

## Beyond OTP

WebAuthn passkeys are strictly better — phishing-resistant, since the credential
is bound to the origin. Swap `auth-otp-form` for `webauthn-authenticator`. The
ACR contract, the API policy and the client code are unchanged; only the flow
differs. That separation is the reason to model levels rather than mechanisms.
