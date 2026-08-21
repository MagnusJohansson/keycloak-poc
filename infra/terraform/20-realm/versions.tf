# ---------------------------------------------------------------------------
# 20-realm : the DocVault realm, as code.
#
# THIS MODULE IS THE POINT OF THE WHOLE REPO.
#
# It configures Keycloak through its Admin REST API, which is identical wherever
# Keycloak runs. So the same HCL, unchanged, provisions either target:
#
#   local  terraform apply -var-file=../../environments/local/realm.tfvars
#   azure  terraform apply -var-file=../../environments/azure/realm.tfvars
#
# Two consequences worth having:
#
#   - the free local lab is a faithful rehearsal for the cloud one. If that ever
#     stops being true, `make seed` fails immediately rather than during a
#     migration.
#   - the realm is reproducible. Someone clicking around the admin console shows
#     up as drift in `make plan`, not as a mystery six months later.
# ---------------------------------------------------------------------------
terraform {
  required_version = ">= 1.9"
  required_providers {
    keycloak = {
      source  = "keycloak/keycloak"
      version = "~> 5.8"
    }
  }
}

provider "keycloak" {
  url = var.keycloak_url

  # Two ways in, because the two environments authenticate differently:
  #
  #  LOCAL  admin username/password against the master realm
  #         (client_id = "admin-cli", the built-in CLI client)
  #
  #  CI / PRODUCTION  a client_credentials service account. Create a confidential
  #         client in the master realm with the `realm-management` roles it
  #         needs, and pass its id and secret here. No human password is ever
  #         involved, which is what you want in an automated pipeline - and it
  #         lets you delete the bootstrap admin entirely.
  client_id     = var.admin_client_id
  username      = var.admin_username      # null when using client_credentials
  password      = var.admin_password      # null when using client_credentials
  client_secret = var.admin_client_secret # null when using username/password
  realm         = var.admin_realm

  # Local Docker Keycloak serves plain HTTP; any real deployment is HTTPS.
  tls_insecure_skip_verify = var.tls_insecure_skip_verify
}
