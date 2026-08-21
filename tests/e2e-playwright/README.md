# End-to-end tests

```bash
make up && make seed        # Keycloak + realm
make api                    # :5001   (separate shell)
make web                    # :5173   (separate shell)

cd tests/e2e-playwright
npm install && npx playwright install chromium
npx playwright test
```

## What these add over the .NET tests

The unit and integration suites mint their own tokens, so they never exercise the
redirect to Keycloak, the authorization-code exchange, or browser cookie
behaviour. These do — against a real Keycloak, in a real browser.

## What is deliberately not automated

Completing an OTP challenge requires enrolling a TOTP secret and computing codes.
The step-up test asserts that the **challenge is issued** — the part that
regresses silently when configuration drifts — and leaves code entry manual. See
[uc3](../../docs/use-cases/uc3-step-up-mfa.md).
