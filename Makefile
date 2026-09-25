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
LOCAL_VARS   := -var-file=$(CURDIR)/infra/environments/local/realm.tfvars
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
up: ## Start Keycloak + Postgres + Mailpit, wait for readiness
	$(COMPOSE) up -d
	@echo "Waiting for Keycloak..."
	@for i in $$(seq 1 60); do \
		if curl -sfo /dev/null http://localhost:8080/realms/master; then \
			echo "Keycloak ready at http://localhost:8080 (admin/admin)"; exit 0; fi; \
		sleep 2; \
	done; echo "Keycloak did not become ready in time; try: make logs"; exit 1

.PHONY: down
down: ## Stop the lab (keeps the database volume)
	$(COMPOSE) down

.PHONY: clean
clean: ## Stop the lab AND delete all data + Terraform state
	$(COMPOSE) down -v
	rm -f $(REALM_DIR)/terraform.tfstate $(REALM_DIR)/terraform.tfstate.backup

.PHONY: logs
logs: ## Tail Keycloak logs
	$(COMPOSE) logs -f keycloak

# --- Realm ------------------------------------------------------------------
.PHONY: seed
seed: ## Apply the DocVault realm to the local Keycloak
	cd $(REALM_DIR) && $(TF) init -backend=false -input=false >/dev/null && $(WS_LOCAL)
	cd $(REALM_DIR) && $(TF) apply -auto-approve -input=false $(LOCAL_VARS)
	@echo
	@$(MAKE) --no-print-directory show-secrets

.PHONY: plan
plan: ## Show what applying the realm would change
	cd $(REALM_DIR) && $(WS_LOCAL) && $(TF) plan -input=false $(LOCAL_VARS)

.PHONY: unseed
unseed: ## Destroy the realm (leaves Keycloak running)
	cd $(REALM_DIR) && $(WS_LOCAL) && $(TF) destroy -auto-approve -input=false $(LOCAL_VARS)

.PHONY: show-secrets
show-secrets: ## Print the generated client secrets and demo logins
	@cd $(REALM_DIR) && $(WS_LOCAL) >/dev/null && \
	echo "Issuer:  $$($(TF) output -raw issuer)" && \
	echo "Worker client secret: $$($(TF) output -raw worker_client_secret)" && \
	echo "Demo users: alice / bob / carol / dave   password: DocVaultLab!2026"

.PHONY: assert-authz
assert-authz: ## Check the Authorization Services model's decisions (needs up + seed)
	./tools/assert-authz-model.sh

.PHONY: export-realm
export-realm: ## Regenerate infra/local/realm-export/docvault-realm.json
	./tools/export-realm.sh

# --- Apps -------------------------------------------------------------------
.PHONY: api
api: ## Run the .NET API on :5001
	ASPNETCORE_URLS=http://localhost:5001 \
		dotnet run --project apps/api-dotnet/DocVault.Api --no-launch-profile

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

# --- Tests ------------------------------------------------------------------
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
