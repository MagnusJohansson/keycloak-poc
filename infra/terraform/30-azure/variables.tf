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
  type        = string
  description = "OIDC issuer from 10-keycloak-azure, e.g. https://ca-keycloak.<region>.azurecontainerapps.io/realms/docvault"
}

variable "container_image" {
  type        = string
  description = "Fully qualified image for the API, e.g. myacr.azurecr.io/docvault-api:1.0.0"
}

variable "tags" {
  type = map(string)
  default = {
    project = "keycloak-poc"
    lab     = "true"
  }
}
