# DocVault - a Keycloak proof-of-concept lab.
# Copyright (C) 2026 Magnus Johansson
# SPDX-License-Identifier: GPL-3.0-or-later
# See the LICENSE file for the full licence text.

variable "resource_group_name" {
  type    = string
  default = "rg-docvault-keycloak"
}

variable "location" {
  type        = string
  default     = "swedencentral"
  description = "Azure region. Put Keycloak close to the applications that validate its tokens, and inside whatever data-residency boundary applies to you."
}

variable "keycloak_version" {
  type        = string
  default     = "26.6.3"
  description = "Keep in step with infra/local/docker-compose.yml so local and cloud behave identically."
}

variable "keycloak_image" {
  type        = string
  default     = null
  description = <<-EOT
    Override to run a pre-built ("optimized") image from your own registry.

    The default pulls upstream Keycloak from quay.io and runs `kc.sh start`,
    which performs the build step on every cold start (~30-60s). That is fine
    for a lab. For production, build the image in Dockerfile once and set this,
    so start-up is fast and reproducible. See docs/14-self-hosting-on-azure.md.
  EOT
}

variable "postgres_sku" {
  type        = string
  default     = "B_Standard_B1ms"
  description = "Burstable is fine for a lab. Use GP_Standard_D2ds_v5 or larger for production - Keycloak is chatty with its database, and jdbc-ping adds cluster-discovery writes."
}

variable "postgres_storage_mb" {
  type    = number
  default = 32768
}

variable "postgres_zone_redundant" {
  type        = bool
  default     = false
  description = <<-EOT
    Zone-redundant HA. Roughly doubles database cost, and REQUIRES a General
    Purpose or Memory Optimized SKU - it is unavailable on Burstable, so
    enabling it without also changing postgres_sku fails at apply (a precondition
    in database.tf catches this at plan time instead).

    Off for the lab. On for anything real: if the database is down, nobody can
    log in to anything, and with jdbc-ping the cluster cannot even form.
  EOT
}

variable "min_replicas" {
  type        = number
  default     = 1
  description = <<-EOT
    Never 0. Keycloak cold-starts slowly and holds authentication sessions in
    memory, so scaling to zero logs everyone out and makes the next login wait
    for a JVM boot.
  EOT

  validation {
    condition     = var.min_replicas >= 1
    error_message = "min_replicas must be at least 1; Keycloak must not scale to zero."
  }
}

variable "max_replicas" {
  type    = number
  default = 3
}

variable "custom_domain" {
  type        = string
  default     = null
  description = <<-EOT
    e.g. "auth.example.com". Optional, but decide it BEFORE onboarding any
    application: the hostname becomes the token issuer, and changing an issuer
    invalidates every token and breaks every configured client.
  EOT
}

variable "admin_username" {
  type    = string
  default = "admin"
}

variable "tags" {
  type = map(string)
  default = {
    project = "keycloak-poc"
    mode    = "azure"
  }
}
