# DocVault - a Keycloak proof-of-concept lab.
# Copyright (C) 2026 Magnus Johansson
# SPDX-License-Identifier: GPL-3.0-or-later
# See the LICENSE file for the full licence text.

# Optimized Keycloak image.
#
# The default in this module pulls upstream Keycloak and runs `kc.sh start`,
# which performs the build step on every cold start (~30-60s). That is fine for
# a lab and wrong for production: start-up is slow, and the build happens at
# runtime where it can fail in a running environment rather than in CI.
#
# Build once, push to ACR, then set:
#   -var="keycloak_image=<your-acr>.azurecr.io/keycloak:26.6.3"
# The module switches the command to `start --optimized` automatically.
#
#   az acr build --registry <acr> --image keycloak:26.6.3 .
FROM quay.io/keycloak/keycloak:26.6.3 AS builder

# Baked in at build time so they cannot drift from what `--optimized` expects.
ENV KC_DB=postgres
ENV KC_HEALTH_ENABLED=true
ENV KC_METRICS_ENABLED=true
ENV KC_CACHE=ispn
ENV KC_CACHE_STACK=jdbc-ping

RUN /opt/keycloak/bin/kc.sh build

FROM quay.io/keycloak/keycloak:26.6.3
COPY --from=builder /opt/keycloak/ /opt/keycloak/

# Runtime-only settings (hostname, DB URL, credentials) stay as environment
# variables - they are deployment-specific and must not be baked into an image.
ENTRYPOINT ["/opt/keycloak/bin/kc.sh"]
