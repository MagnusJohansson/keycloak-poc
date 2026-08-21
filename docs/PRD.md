# Overview

In this repository I want to create a comprehensive proof of concept (or lab)
showcasing how to set up and use **[Keycloak](https://www.keycloak.org/)** — the
open-source identity and access management server — in a real-world scenario.
The goal is a step-by-step guide for developers and security enthusiasts to
understand Keycloak's capabilities and how to integrate it into their
applications and systems.

Keycloak provides single sign-on, standards-based authentication (OpenID
Connect, OAuth 2.0, SAML 2.0), multi-factor authentication, federation with
external identity providers, and fine-grained authorization. It is an
**authentication and authorization** server: it decides *who a user is* and
*what they are allowed to do*, and issues signed tokens stating the answer. It
does not encrypt application data or transport messages.

The proof of concept covers:

1. **Introduction to Keycloak** — what it is, its features, and the problems it
   solves in identity and access management.
2. **Setup and Installation** — running Keycloak locally with Docker, its
   prerequisites and configuration, and defining a realm reproducibly as code
   rather than by clicking through the admin console.
3. **Integration with Applications** — integrating Keycloak into web
   applications, mobile apps, desktop apps and backend services, with code
   samples, API usage and secure-integration practice.
4. **Use Cases and Scenarios** — real-world scenarios, each with the problem,
   the realm configuration, the application code, and how to verify it:
   - role-based access control and per-resource authorization for document sharing
   - enterprise SSO by federating an external identity provider (Microsoft Entra ID)
   - step-up authentication (requiring MFA for sensitive operations only)
   - machine-to-machine authentication for background services
   - user consent and data minimisation, including pairwise subject identifiers
   - audit logging and forwarding security events to a SIEM
5. **Testing and Validation** — unit, integration, end-to-end and security
   testing of the integration, including deliberately malformed and forged
   tokens.
6. **Troubleshooting and Support** — common failures during setup and
   integration, with symptom → cause → fix, and where to get help.
7. **Conclusion and Next Steps** — key takeaways and suggestions for going
   further.

## Server side

I want to run Keycloak in Azure, so the lab includes deploying it to a cloud
environment with a focus on Azure: the necessary configuration, resource
allocation, networking, secret management, and practices for running it
scalably and securely.

The lab should also run entirely locally at no cost, so that anyone can follow
it without a cloud subscription — and so that the local and cloud deployments
are provably the same configuration rather than two things that have drifted
apart.

## Client side

On the client side I want examples of setting up and using Keycloak in
different client environments, including web browsers, mobile devices and
desktop applications: installing the required libraries or SDKs, configuring
the client to talk to the Keycloak server, and implementing secure
authentication and authorization in the client application.

Specifically:

- **Web Applications** — integrating Keycloak using VueJS and ReactJS,
  including code examples and practices for handling tokens safely in a browser.
- **Mobile Applications** — integrating Keycloak into apps built with Flutter
  and React Native, including secure token storage and communication with the
  Keycloak server.
- **Desktop Applications** — integrating Keycloak into desktop applications,
  with an example for Electron, focusing on keeping credentials out of reach of
  the application's own UI layer.
