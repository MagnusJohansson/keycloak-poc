# DocVault - a Keycloak proof-of-concept lab.
# Copyright (C) 2026 Magnus Johansson
# SPDX-License-Identifier: GPL-3.0-or-later
# See the LICENSE file for the full licence text.

# ---------------------------------------------------------------------------
# DocVault - a Keycloak identity lab.
#
#   make up && make seed && make api      # then, in another shell: make web
#
# Everything here targets the LOCAL Docker Keycloak so the lab costs nothing.
# The cloud equivalents are the azure-* targets.
# ---------------------------------------------------------------------------
SHELL := /bin/bash
.DEFAULT_GOAL := help

COMPOSE  := docker compose -f infra/local/docker-compose.yml
TF       := terraform
REALM_DIR    := infra/terraform/20-realm
KEYCLOAK_AZURE_DIR := infra/terraform/10-keycloak-azure
APPS_AZURE_DIR     := infra/terraform/30-azure
FGA_DIR      := infra/terraform/25-openfga
LOCAL_VARS   := -var-file=$(CURDIR)/infra/environments/local/realm.tfvars
FGA_VARS     := -var-file=$(CURDIR)/infra/environments/local/openfga.tfvars
# OpenFGA's CLI, run from its image so the model tests need no install.
FGA_CLI      := docker run --rm -v $(CURDIR)/infra/openfga:/w -w /w $${FGA_CLI_IMAGE:-mirror.gcr.io/openfga/cli:v0.8.2}
AZURE_VARS   := -var-file=$(CURDIR)/infra/environments/azure/realm.tfvars

# 20-realm is ONE module applied to MORE THAN ONE Keycloak, so each target picks
# a Terraform workspace first. Without this both environments share
# terraform.tfstate: seeding Azure silently overwrites the local realm's state,
# leaving the local realm untracked and the cloud realm un-destroyable.
# Workspaces keep the two in terraform.tfstate.d/<name>/ and need no remote backend.
WS_LOCAL     := $(TF) workspace select -or-create local
WS_AZURE     := $(TF) workspace select -or-create azure

.PHONY: help
help: ## Show this help
	@grep -hE '^[a-zA-Z0-9_-]+:.*?## ' $(MAKEFILE_LIST) \
		| awk 'BEGIN{FS=":.*?## "}{printf "  \033[36m%-18s\033[0m %s\n", $$1, $$2}'

# --- Local lab --------------------------------------------------------------
.PHONY: up
up: ## Start Keycloak + Postgres + the directory + OpenFGA + Mailpit, wait for readiness
	$(COMPOSE) up -d
	@echo "Waiting for Keycloak..."
	@for i in $$(seq 1 60); do \
		if curl -sfo /dev/null http://localhost:8080/realms/master; then \
			echo "Keycloak ready at http://localhost:8080 (admin/admin)"; break; fi; \
		[ $$i = 60 ] && { echo "Keycloak did not become ready in time; try: make logs"; exit 1; }; \
		sleep 2; \
	done
	@# /healthz needs no key; every other call does.
	@for i in $$(seq 1 30); do \
		if curl -sf http://localhost:8090/healthz | grep -q SERVING; then \
			echo "OpenFGA ready at http://localhost:8090"; exit 0; fi; \
		sleep 2; \
	done; echo "OpenFGA did not become ready in time; try: $(COMPOSE) logs openfga"; exit 1

.PHONY: down
down: ## Stop the lab (keeps the database volume)
	$(COMPOSE) down

.PHONY: clean
clean: ## Stop the lab AND delete all data + Terraform state
	$(COMPOSE) down -v
	@# The LOCAL workspace's state only, never azure's: that one tracks billable
	@# resources that `down -v` did not touch. Since the workspaces, the realm's
	@# state lives in terraform.tfstate.d/local; this target used to remove only
	@# terraform.tfstate, which is where state lived before them, and is kept for
	@# a checkout that still has one.
	rm -rf $(REALM_DIR)/terraform.tfstate.d/local $(FGA_DIR)/terraform.tfstate.d/local
	rm -f $(REALM_DIR)/terraform.tfstate $(REALM_DIR)/terraform.tfstate.backup

.PHONY: logs
logs: ## Tail Keycloak logs
	$(COMPOSE) logs -f keycloak

# --- Realm ------------------------------------------------------------------
.PHONY: seed
seed: ## Apply the DocVault realm to the local Keycloak, then the OpenFGA store and model
	cd $(REALM_DIR) && $(TF) init -backend=false -input=false >/dev/null && $(WS_LOCAL)
	cd $(REALM_DIR) && $(TF) apply -auto-approve -input=false $(LOCAL_VARS)
	cd $(FGA_DIR) && $(TF) init -backend=false -input=false >/dev/null && $(WS_LOCAL)
	@# OpenFGA's data gone (its volume removed without `make clean`) while the
	@# state still holds the store: the provider (0.5.2) drops the missing store,
	@# then fails reading the model with "authorization_model_not_found", and no
	@# apply gets past it. Forget the state, but only when OpenFGA itself says
	@# the store does not exist.
	@cd $(FGA_DIR) && id=$$($(TF) output -raw store_id 2>/dev/null) && [ -n "$$id" ] && \
	[ "$$(curl -s -o /dev/null -w '%{http_code}' -H "Authorization: Bearer $${OPENFGA_API_KEY:-docvault-fga-lab-key}" \
		http://localhost:8090/stores/$$id)" = 404 ] && { \
		echo "OpenFGA has no store $$id any more; forgetting it in Terraform's state."; \
		$(TF) state rm openfga_authorization_model.docvault openfga_store.docvault >/dev/null; } || true
	cd $(FGA_DIR) && $(TF) apply -auto-approve -input=false $(FGA_VARS)
	@echo
	@$(MAKE) --no-print-directory show-secrets

.PHONY: plan
plan: ## Show what applying the realm and the OpenFGA model would change
	cd $(REALM_DIR) && $(WS_LOCAL) && $(TF) plan -input=false $(LOCAL_VARS)
	cd $(FGA_DIR) && $(WS_LOCAL) && $(TF) plan -input=false $(FGA_VARS)

.PHONY: unseed
unseed: ## Destroy the realm and the OpenFGA store (leaves Keycloak and OpenFGA running)
	cd $(FGA_DIR) && $(WS_LOCAL) && $(TF) destroy -auto-approve -input=false $(FGA_VARS)
	cd $(REALM_DIR) && $(WS_LOCAL) && $(TF) destroy -auto-approve -input=false $(LOCAL_VARS)

.PHONY: show-secrets
show-secrets: ## Print the generated client secrets and demo logins
	@cd $(REALM_DIR) && $(WS_LOCAL) >/dev/null && \
	echo "Issuer:  $$($(TF) output -raw issuer)" && \
	echo "Worker client secret: $$($(TF) output -raw worker_client_secret)" && \
	echo "Demo users: alice / bob / carol / dave   password: DocVaultLab!2026"
	@cd $(FGA_DIR) && $(WS_LOCAL) >/dev/null && \
	echo "OpenFGA store: $$($(TF) output -raw store_id)   model: $$($(TF) output -raw model_id)"

.PHONY: assert-authz
assert-authz: ## Check the Authorization Services model's decisions (needs up + seed)
	./tools/assert-authz-model.sh

.PHONY: export-realm
export-realm: ## Regenerate infra/local/realm-export/docvault-realm.json
	./tools/export-realm.sh

# --- Apps -------------------------------------------------------------------
.PHONY: api
api: ## Run the .NET API on :5001
	@mkdir -p .lab
	@# Also to a file: `make assert-mcp` matches the API's access log with the
	@# MCP server's audit log by trace id.
	ASPNETCORE_URLS=http://localhost:5001 \
		dotnet run --project apps/api-dotnet/DocVault.Api --no-launch-profile 2>&1 | tee .lab/api.log

.PHONY: web
web: ## Run the React SPA on :5173
	@# Without .env the app throws at import time and renders a BLANK page - the
	@# message only reaches the browser console, so it reads as "the lab is broken".
	@test -f apps/web-react/.env || { \
	  cp apps/web-react/.env.example apps/web-react/.env; \
	  echo "Created apps/web-react/.env from .env.example (local lab defaults)."; }
	cd apps/web-react && npm run dev

.PHONY: web-vue
web-vue: ## Run the Vue SPA on :5174
	@test -f apps/web-vue/.env || { \
	  cp apps/web-vue/.env.example apps/web-vue/.env; \
	  echo "Created apps/web-vue/.env from .env.example (local lab defaults)."; }
	cd apps/web-vue && npm run dev

.PHONY: worker
worker: ## Run the background worker (client_credentials demo)
	dotnet run --project apps/api-dotnet/DocVault.Worker

.PHONY: mcp
mcp: ## Run the MCP server on :5002 (needs up + seed + api)
	@cd $(REALM_DIR) && $(WS_LOCAL) >/dev/null
	@mkdir -p .lab
	ASPNETCORE_URLS=http://localhost:5002 \
	Audit__File="$(CURDIR)/.lab/mcp-audit.jsonl" \
	Mcp__ClientSecret="$$(cd $(REALM_DIR) && $(TF) output -raw mcp_server_client_secret)" \
		dotnet run --project apps/mcp-dotnet/DocVault.Mcp --no-launch-profile

.PHONY: assert-mcp
assert-mcp: ## Check MCP authorization end to end (needs up + seed + api + mcp)
	@cd $(REALM_DIR) && $(WS_LOCAL) >/dev/null
	MCP_CLIENT_SECRET="$$(cd $(REALM_DIR) && $(TF) output -raw mcp_server_client_secret)" \
	MCP_AUDIT_LOG="$(CURDIR)/.lab/mcp-audit.jsonl" \
	DOCVAULT_API_LOG="$$(test -f .lab/api.log && echo "$(CURDIR)/.lab/api.log")" \
		python3 tools/assert-mcp-authz.py

# --- The directory (LDAP) -----------------------------------------------------
.PHONY: assert-ldap
assert-ldap: ## Check LDAP federation end to end (needs up + seed + api). Changes the lab's directory and puts it back.
	python3 tools/assert-ldap.py

.PHONY: ldap-search
ldap-search: ## Print the lab directory's people and groups (as its administrator)
	@$(COMPOSE) exec -T ldap ldapsearch -Q -LLL -Y EXTERNAL -H ldapi://%2Frun%2Fopenldap%2Fldapi \
		-b dc=docvault,dc=test '(|(objectClass=inetOrgPerson)(objectClass=groupOfNames))' uid cn mail member

# --- The legacy SAML application ------------------------------------------------
.PHONY: reports
reports: ## Run the legacy SAML application on :5003 (needs up + seed)
	@cd $(REALM_DIR) && $(WS_LOCAL) >/dev/null
	ASPNETCORE_URLS=http://localhost:5003 \
	Saml__IdpCertificate="$$(cd $(REALM_DIR) && $(TF) output -raw saml_idp_certificate)" \
	Saml__SigningCertificate="$$(cd $(REALM_DIR) && $(TF) output -raw reports_saml_signing_certificate)" \
	Saml__SigningKey="$$(cd $(REALM_DIR) && $(TF) output -raw reports_saml_signing_key)" \
		dotnet run --project apps/saml-reports/DocVault.Reports --no-launch-profile

.PHONY: assert-saml-app
assert-saml-app: ## Check the legacy SAML application end to end (needs up + seed + reports)
	python3 tools/assert-saml-app.py

# --- The legacy SAML provider -------------------------------------------------
.PHONY: assert-saml
assert-saml: ## Check the brokered SAML provider end to end (needs up + seed + api). Adds test people to the stand-in legacy realm and removes them.
	python3 tools/assert-saml.py

# --- Tests ------------------------------------------------------------------
.PHONY: test-model
test-model: ## Test the OpenFGA model on OpenFGA's own engine (Docker; no running lab needed)
	$(FGA_CLI) model test --tests docvault.fga.yml

.PHONY: test
test: ## Unit + integration + security tests (no Docker, no network)
	dotnet test apps/api-dotnet/DocVault.slnx
	dotnet test apps/desktop-winui/DocVault.Desktop.Auth.Tests

.PHONY: winui-test
winui-test: ## Test the desktop OIDC library (cross-platform; no Windows needed)
	dotnet test apps/desktop-winui/DocVault.Desktop.Auth.Tests

.PHONY: winui
winui: ## Run the WinUI 3 client. WINDOWS ONLY - XAML will not compile elsewhere.
	dotnet run --project apps/desktop-winui/DocVault.WinUI

.PHONY: e2e
e2e: ## Playwright browser tests (requires: make up, seed, api, web)
	cd tests/e2e-playwright && npx playwright test

.PHONY: token
token: ## Mint a client_credentials token and decode it
	./tools/decode-token.sh

.PHONY: license-check
license-check: ## Verify every source file carries the SPDX licence header
	./tools/license-header.sh --check

.PHONY: license-apply
license-apply: ## Add the SPDX licence header wherever it is missing
	./tools/license-header.sh

# --- Azure ------------------------------------------------------------------
# Two separate deployments, applied in order:
#   1. Keycloak itself   (10-keycloak-azure)
#   2. your applications (30-azure)
.PHONY: azure-plan
azure-plan: ## Plan Keycloak on Azure (read-only; needs `az login`)
	cd $(KEYCLOAK_AZURE_DIR) && $(TF) init -input=false && $(TF) plan -input=false

# NOTE: the azure-* targets deliberately do NOT pass -auto-approve. They create
# billable resources, so Terraform's "yes" prompt is the last chance to read the
# plan. (They also must not pass -input=false, which suppresses that prompt and
# then fails with "error asking for approval: EOF".)
.PHONY: azure-apply
azure-apply: ## Deploy Keycloak into your Azure subscription, then write realm.tfvars
	cd $(KEYCLOAK_AZURE_DIR) && $(TF) init && $(TF) apply
	cd $(KEYCLOAK_AZURE_DIR) && $(TF) output -raw realm_tfvars \
		> $(CURDIR)/infra/environments/azure/realm.tfvars
	@echo "Wrote infra/environments/azure/realm.tfvars"
	@echo "Now: make seed-azure"

.PHONY: seed-azure
seed-azure: ## Apply the SAME realm module to your Azure Keycloak
	cd $(REALM_DIR) && $(TF) init && $(WS_AZURE)
	cd $(REALM_DIR) && $(TF) apply $(AZURE_VARS)

.PHONY: azure-secrets
azure-secrets: ## Print the Azure realm's issuer and generated client secrets
	@cd $(REALM_DIR) && $(WS_AZURE) >/dev/null && \
	echo "Issuer:  $$($(TF) output -raw issuer)" && \
	echo "Worker client secret: $$($(TF) output -raw worker_client_secret)"

.PHONY: azure-destroy
azure-destroy: ## Tear down EVERYTHING deployed to Azure (realm, apps, Keycloak)
	@# Order matters. The realm lives INSIDE Keycloak, so it must be destroyed
	@# while Keycloak is still running - otherwise 20-realm's azure workspace is
	@# left holding state for objects that no longer exist, and the next
	@# `make seed-azure` fails with "409 Realm docvault already exists" or tries
	@# to delete resources it can no longer reach.
	@#
	@# This target used to destroy only 10-keycloak-azure, silently leaving
	@# rg-docvault-lab (API container app, ACR, Static Web Apps, Log Analytics)
	@# running and billing. Keep all three steps.
	-cd $(REALM_DIR) && $(TF) init && $(WS_AZURE) && $(TF) destroy $(AZURE_VARS)
	-cd $(APPS_AZURE_DIR) && $(TF) init && $(TF) destroy
	cd $(KEYCLOAK_AZURE_DIR) && $(TF) init && $(TF) destroy
	@echo
	@echo "Confirm nothing is left:  az group list --query \"[?starts_with(name,'rg-docvault')].name\" -o tsv"

.PHONY: azure-destroy-keycloak
azure-destroy-keycloak: ## Tear down ONLY Keycloak (10-keycloak-azure), leaving your apps
	cd $(KEYCLOAK_AZURE_DIR) && $(TF) init && $(TF) destroy

.PHONY: apps-plan
apps-plan: ## Plan the Azure resources that host your apps (30-azure)
	cd $(APPS_AZURE_DIR) && $(TF) init && $(TF) plan

.PHONY: android-reverse
android-reverse: ## Map localhost on a connected Android device/emulator to this machine
	@# Keycloak derives `iss` from the request host, and the API trusts exactly one
	@# issuer. Reaching Keycloak as 10.0.2.2 therefore mints tokens the API rejects
	@# with IDX10205. Forwarding the device's own localhost keeps the issuer identical.
	@# adb clears these when the emulator or the adb server restarts.
	adb reverse tcp:8080 tcp:8080
	adb reverse tcp:5001 tcp:5001
	@adb reverse --list
