variable "resource_group_name" {
  type    = string
  default = "rg-docvault-lab"
}

variable "location" {
  type        = string
  default     = "swedencentral"
  description = "Azure region. Use the same region as the Keycloak deployment so token validation is not a cross-region round trip."
}

variable "keycloak_issuer" {
  default     = null
  type        = string
  description = "OIDC issuer from 10-keycloak-azure, e.g. https://ca-keycloak.<region>.azurecontainerapps.io/realms/docvault"
}

variable "container_image" {
  default     = null
  type        = string
  description = "Fully qualified image for the API, e.g. myacr.azurecr.io/docvault-api:1.0.0"
}

variable "enable_sentinel" {
  type        = bool
  default     = false
  description = <<-EOT
    Onboard the Log Analytics workspace to Microsoft Sentinel.

    OFF by default because Sentinel bills per GB analysed, which makes it the
    least predictable line in this deployment - a lab someone copies should not
    quietly start metered billing. A new workspace is free for 31 days.

    Everything else works without it: Container Apps already streams Keycloak's
    stdout into Log Analytics, so the events are collected and queryable either
    way. Sentinel adds the SIEM layer on top - analytics rules, incidents,
    hunting. Turn it on to follow use-case 6 properly.
  EOT
}

variable "static_web_app_location" {
  type        = string
  default     = "westeurope"
  description = <<-EOT
    Azure region for the Static Web Apps.

    SEPARATE from `location` because Static Web Apps exist in only a handful of
    regions - deploying to any other (swedencentral, for instance) fails with
    LocationNotAvailableForResourceType. This costs nothing in latency: static
    content is served from a global CDN, so the region only decides where the
    build/metadata service lives.
  EOT

  validation {
    condition = contains(
      ["centralus", "eastus2", "westus2", "westeurope", "eastasia"],
      var.static_web_app_location
    )
    error_message = "Static Web Apps are only available in centralus, eastus2, westus2, westeurope or eastasia. Azure may add more; check `az provider show -n Microsoft.Web` if this looks stale."
  }
}

variable "tags" {
  type = map(string)
  default = {
    project = "keycloak-poc"
    lab     = "true"
  }
}
