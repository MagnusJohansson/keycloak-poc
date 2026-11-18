#!/usr/bin/env python3
# DocVault - a Keycloak proof-of-concept lab.
# Copyright (C) 2026 Magnus Johansson
# SPDX-License-Identifier: GPL-3.0-or-later
# See the LICENSE file for the full licence text.

"""End-to-end check of the brokered SAML identity provider against the REAL stack.

Needs: `make up`, `make seed`, and the API on :5001. `make assert-saml` runs
it; CI runs it in the realm job.

DocVault trusts a legacy SAML identity provider (a stand-in: a second realm,
see infra/terraform/20-realm/saml-legacy-idp.tf). A sign-in that works proves
only that the two can talk. What matters is what DocVault REFUSES: this plays
the browser between the two, so it can hand DocVault a SAML response that has
been stripped of its signature, altered, wrapped, replayed, or that was issued
for somebody else, the way an attacker in that position could.

It cannot forge a signature, and does not try: every bad response here is a
real one from the legacy provider, changed afterwards.

It adds people and a client to the stand-in legacy realm and removes them,
also when a check fails or the run is interrupted. It never changes DocVault's
own users, except to delete the brokered test users it caused.

Standard library only.
"""
import base64
import hashlib
import html
import json
import os
import re
import secrets
import signal
import sys
import urllib.error
import urllib.parse
import urllib.request

KEYCLOAK = os.environ.get("KEYCLOAK_URL", "http://localhost:8080")
REALM = os.environ.get("KEYCLOAK_REALM", "docvault")
LEGACY = "legacy-idp"
ALIAS = "legacy-saml"
API = os.environ.get("DOCVAULT_API_URL", "http://localhost:5001")
MAILPIT = os.environ.get("MAILPIT_URL", "http://localhost:8025")
CLIENT = "docvault-web-react"
REDIRECT = "http://localhost:5173/callback"  # nothing needs to listen
PASSWORD = os.environ.get("SEED_USER_PASSWORD", "DocVaultLab!2026")
OTHER_PASSWORD = "Legacy!Password2026"  # for the people this script adds
TOKEN_URL = f"{KEYCLOAK}/realms/{REALM}/protocol/openid-connect/token"
ACS = f"{KEYCLOAK}/realms/{REALM}/broker/{ALIAS}/endpoint"
ENTITY_ID = f"{KEYCLOAK}/realms/{REALM}"
ADMIN_USER = os.environ.get("KEYCLOAK_ADMIN_USER", "admin")
ADMIN_PASSWORD = os.environ.get("KEYCLOAK_ADMIN_PASSWORD", "admin")

passed = failed = 0


def check(label, ok, detail=""):
    global passed, failed
    passed, failed = passed + bool(ok), failed + (not ok)
    print(f"  {'PASS' if ok else 'FAIL'}  {label}" + (f"  [{detail}]" if detail else ""))


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


def http(method, url, data=None, headers=None, form=False):
    """One request, redirects not followed. Returns (status, headers, body)."""
    headers = dict(headers or {})
    body = None
    if data is not None:
        if form:
            body = urllib.parse.urlencode(data).encode()
            headers["Content-Type"] = "application/x-www-form-urlencoded"
        else:
            body = json.dumps(data).encode()
            headers["Content-Type"] = "application/json"
    request = urllib.request.Request(url, data=body, headers=headers, method=method)
    try:
        response = urllib.request.build_opener(NoRedirect()).open(request, timeout=60)
    except urllib.error.HTTPError as error:
        response = error
    return response.status, response.headers, response.read().decode()


class Browser:
    """Cookies kept by path. The two realms are on one host and use the same
    cookie names; a jar keyed by name alone hands DocVault the legacy realm's
    session cookie, and the sign-in ends in "cookie not found"."""

    def __init__(self):
        self.cookies = {}

    def request(self, method, url, data=None):
        path = urllib.parse.urlparse(url).path
        matching = sorted(((n, p, v) for (n, p), v in self.cookies.items() if path.startswith(p)), key=lambda c: len(c[1]))
        headers = {"Cookie": "; ".join(f"{n}={v}" for n, _, v in matching)} if matching else {}
        status, response_headers, body = http(method, url, data, headers, form=data is not None)
        for line in response_headers.get_all("Set-Cookie") or []:
            parts = [part.strip() for part in line.split(";")]
            name, _, value = parts[0].partition("=")
            cookie_path = next((p[5:] for p in parts[1:] if p.lower().startswith("path=")), "/")
            if value:
                self.cookies[(name, cookie_path)] = value
            else:
                self.cookies.pop((name, cookie_path), None)
        return status, response_headers, body


def page_text(body, limit=160):
    text = re.sub(r"<script.*?</script>|<style.*?</style>|<[^>]+>", " ", body, flags=re.S)
    return re.sub(r"\s+", " ", html.unescape(text)).replace("Sign in to DocVault DocVault ", "").strip()[:limit]


def first_form(body):
    """The first form on a page: (action, {input name: value})."""
    form = re.search(r'<form[^>]*action="([^"]+)"[^>]*>(.*?)</form>', body, re.S | re.I)
    if not form:
        return None, {}
    fields = {html.unescape(name): html.unescape(value)
              for name, value in re.findall(r'<input[^>]*name="([^"]+)"[^>]*value="([^"]*)"', form.group(2), re.I)}
    return html.unescape(form.group(1)), fields


def claims(jwt):
    payload = jwt.split(".")[1]
    return json.loads(base64.urlsafe_b64decode(payload + "=" * (-len(payload) % 4)))


def sign_in(username, password=PASSWORD, change_response=None, change_request=None, seen=None):
    """Sign in to DocVault through the legacy SAML provider, as a browser would.

    change_response / change_request get the form fields of the SAML message
    in transit and return what is sent on instead: the attacker's hands.
    `seen`, a dict, receives the untouched SAMLRequest and SAMLResponse.

    Returns the token endpoint's JSON, or {"error": ..., "page": ...}.
    """
    browser = Browser()
    verifier = secrets.token_urlsafe(48)
    challenge = base64.urlsafe_b64encode(hashlib.sha256(verifier.encode()).digest()).rstrip(b"=").decode()
    query = {
        "client_id": CLIENT, "response_type": "code", "redirect_uri": REDIRECT, "scope": "openid profile email",
        "code_challenge": challenge, "code_challenge_method": "S256", "state": "s",
    }
    status, headers, body = browser.request(
        "GET", f"{KEYCLOAK}/realms/{REALM}/protocol/openid-connect/auth?" + urllib.parse.urlencode(query))
    link = re.search(rf'href="([^"]*broker/{ALIAS}/login[^"]*)"', body)
    if not link:
        return {"error": "no_provider_button", "page": page_text(body)}
    status, headers, body = browser.request("GET", urllib.parse.urljoin(KEYCLOAK, html.unescape(link.group(1))))
    for _ in range(14):
        if status in (302, 303):
            if not headers["Location"].startswith(KEYCLOAK):
                break
            status, headers, body = browser.request("GET", headers["Location"])
            continue
        action, fields = first_form(body)
        if not action:
            break
        if "SAMLRequest" in fields:
            if seen is not None:
                seen["SAMLRequest"] = fields["SAMLRequest"]
            if change_request:
                fields = change_request(fields)
        elif "SAMLResponse" in fields:
            if seen is not None:
                seen["SAMLResponse"] = fields["SAMLResponse"]
            if change_response:
                fields = change_response(fields)
        elif 'value="linkAccount"' in body:
            # Keycloak's built-in first-sign-in flow asks "Account already exists.
            # Add to existing account?" An attacker says yes.
            fields = {"submitAction": "linkAccount"}
        elif 'name="password"' in body:
            fields = {"username": username, "password": password}
        else:
            break  # a page this script does not answer: a refusal, or a question
        status, headers, body = browser.request("POST", urllib.parse.urljoin(KEYCLOAK, action), fields)
    if status not in (302, 303):
        return {"error": "refused", "status": status, "page": page_text(body)}
    params = urllib.parse.parse_qs(urllib.parse.urlparse(headers["Location"]).query)
    if "code" not in params:
        return {"error": params.get("error", ["no_code"])[0]}
    _, _, body = http("POST", TOKEN_URL, {
        "grant_type": "authorization_code", "client_id": CLIENT, "code": params["code"][0],
        "redirect_uri": REDIRECT, "code_verifier": verifier,
    }, form=True)
    return json.loads(body)


def local_sign_in(username, password=PASSWORD):
    """A DocVault user signing in with a password, not through the provider."""
    browser = Browser()
    verifier = secrets.token_urlsafe(48)
    challenge = base64.urlsafe_b64encode(hashlib.sha256(verifier.encode()).digest()).rstrip(b"=").decode()
    query = {
        "client_id": CLIENT, "response_type": "code", "redirect_uri": REDIRECT, "scope": "openid",
        "code_challenge": challenge, "code_challenge_method": "S256", "state": "s",
    }
    status, headers, body = browser.request(
        "GET", f"{KEYCLOAK}/realms/{REALM}/protocol/openid-connect/auth?" + urllib.parse.urlencode(query))
    action, _ = first_form(body)
    status, headers, body = browser.request("POST", action, {"username": username, "password": password})
    for _ in range(5):
        if status in (302, 303) and headers["Location"].startswith(KEYCLOAK):
            status, headers, body = browser.request("GET", headers["Location"])
        else:
            break
    if status not in (302, 303) or "code=" not in headers["Location"]:
        return {"error": "refused", "status": status}
    code = urllib.parse.parse_qs(urllib.parse.urlparse(headers["Location"]).query)["code"][0]
    _, _, body = http("POST", TOKEN_URL, {
        "grant_type": "authorization_code", "client_id": CLIENT, "code": code,
        "redirect_uri": REDIRECT, "code_verifier": verifier,
    }, form=True)
    return json.loads(body)


def refresh(refresh_token):
    status, _, body = http("POST", TOKEN_URL, {
        "grant_type": "refresh_token", "client_id": CLIENT, "refresh_token": refresh_token,
    }, form=True)
    return status, json.loads(body)


def describe(response):
    """A response for a log line. Never a token, not even part of one."""
    if "access_token" in response:
        token = claims(response["access_token"])
        return f"a token for {token.get('preferred_username')} {token.get('groups') or []}"
    return f"{response.get('error')} {response.get('status', '')} {response.get('page', '')}".strip()[:150]


def groups_and_roles(response):
    if "access_token" not in response:
        return None, None
    token = claims(response["access_token"])
    return token.get("groups") or [], sorted(token.get("resource_access", {}).get("docvault-api", {}).get("roles", []))


def api(token):
    status, _, body = http("GET", API + "/documents", headers={"Authorization": f"Bearer {token}"})
    try:
        return status, json.loads(body) if body else {}
    except json.JSONDecodeError:
        return status, {}


def admin(method, path, data=None, realm=REALM):
    _, _, body = http("POST", f"{KEYCLOAK}/realms/master/protocol/openid-connect/token", {
        "grant_type": "password", "client_id": "admin-cli", "username": ADMIN_USER, "password": ADMIN_PASSWORD,
    }, form=True)
    headers = {"Authorization": "Bearer " + json.loads(body)["access_token"]}
    status, _, body = http(method, f"{KEYCLOAK}/admin/realms/{realm}{path}", data, headers=headers)
    try:
        return status, json.loads(body) if body else None
    except json.JSONDecodeError:
        return status, body


def user(username, realm=REALM):
    _, users = admin("GET", "/users?exact=true&username=" + urllib.parse.quote(username), realm=realm)
    return users[0] if users else None


def remove_user(username, realm):
    found = user(username, realm)
    if found:
        admin("DELETE", f"/users/{found['id']}", realm=realm)


def add_legacy_person(username, email):
    status, _ = admin("POST", "/users", {
        "username": username, "email": email, "emailVerified": True, "enabled": True,
        "firstName": "Test", "lastName": "Person",
        "credentials": [{"type": "password", "value": OTHER_PASSWORD, "temporary": False}],
    }, realm=LEGACY)
    return status == 201


def mail_to(address):
    """How many messages the lab's mail catcher holds for an address."""
    try:
        with urllib.request.urlopen(f"{MAILPIT}/api/v1/search?query=" + urllib.parse.quote(f"to:{address}"), timeout=10) as r:
            return json.load(r).get("total", 0)
    except (urllib.error.URLError, OSError, ValueError):
        return None


# --- The attacker's hands: changes to a real response, in transit -------------
SIGNATURE = re.compile(r"<dsig:Signature\b.*?</dsig:Signature>", re.S)


def xml_of(fields, name="SAMLResponse"):
    return base64.b64decode(fields[name]).decode()


def with_xml(fields, xml, name="SAMLResponse"):
    return dict(fields, **{name: base64.b64encode(xml.encode()).decode()})


def parts(xml):
    """(before the assertion, the assertion, after it)."""
    start = xml.index("<saml:Assertion")
    end = xml.index("</saml:Assertion>") + len("</saml:Assertion>")
    return xml[:start], xml[start:end], xml[end:]


def no_signatures(fields):
    return with_xml(fields, SIGNATURE.sub("", xml_of(fields)))


def no_assertion_signature(fields):
    before, assertion, after = parts(xml_of(fields))
    return with_xml(fields, before + SIGNATURE.sub("", assertion) + after)


def no_envelope_signature(fields):
    before, assertion, after = parts(xml_of(fields))
    return with_xml(fields, SIGNATURE.sub("", before) + assertion + after)


def other_group(fields):
    return with_xml(fields, xml_of(fields).replace(">ACME-ENG<", ">GLOBEX<"))


def other_person(fields):
    return with_xml(fields, xml_of(fields).replace(">heidi<", ">ivan<"))


def forged_copy(assertion):
    """The assertion without its signature, saying something else."""
    return (SIGNATURE.sub("", assertion).replace(">heidi<", ">ivan<").replace(">ACME-ENG<", ">GLOBEX<")
            .replace('ID="ID_', 'ID="ID_forged', 1))


def forged_first(fields):
    """Signature wrapping: a forged assertion first, the signed one left in place after it."""
    before, assertion, after = parts(xml_of(fields))
    return with_xml(fields, SIGNATURE.sub("", before) + forged_copy(assertion) + assertion + after)


def forged_around(fields):
    """Signature wrapping: a forged assertion that carries the signed one inside it."""
    before, assertion, after = parts(xml_of(fields))
    wrapped = forged_copy(assertion).replace(
        "</saml:Assertion>", "<saml:Advice>" + assertion + "</saml:Advice></saml:Assertion>")
    return with_xml(fields, SIGNATURE.sub("", before) + wrapped + after)


def main():
    _, provider = admin("GET", f"/identity-provider/instances/{ALIAS}")
    if not isinstance(provider, dict) or "config" not in provider:
        sys.exit("The realm has no brokered SAML provider (make seed, with saml_legacy_idp_enabled = true).")
    config = provider["config"]

    # ------------------------------------------------------------------ #
    print("\nWhat DocVault asks of the legacy provider")
    check("it checks signatures, and wants the assertion itself signed",
          config.get("validateSignature") == "true" and config.get("wantAssertionsSigned") == "true")
    check("it signs its own requests", config.get("wantAuthnRequestsSigned") == "true")
    check("it re-applies the mappers at every sign-in", config.get("syncMode") == "FORCE", str(config.get("syncMode")))
    check("it does not take the provider's word for an email address", provider.get("trustEmail") is False)
    check("a first sign-in cannot link to an existing account",
          provider.get("firstBrokerLoginFlowAlias") == "first-broker-login-no-link",
          str(provider.get("firstBrokerLoginFlowAlias")))

    # ------------------------------------------------------------------ #
    print("\nPeople from the legacy provider sign in")
    seen = {}
    heidi = sign_in("heidi", seen=seen)
    check("heidi signs in at the legacy provider and gets a DocVault token", "access_token" in heidi, describe(heidi))
    if "access_token" not in heidi:
        return
    token = claims(heidi["access_token"])
    groups, roles = groups_and_roles(heidi)
    check("the token is DocVault's own", token.get("iss") == ENTITY_ID and "docvault-api" in token.get("aud", []),
          str(token.get("iss")))
    check("her username carries the provider's name", token.get("preferred_username") == f"{ALIAS}.heidi",
          str(token.get("preferred_username")))
    check("the legacy group ACME-ENG became /acme/engineering, with its roles",
          groups == ["/acme/engineering"] and roles == ["doc.editor", "doc.reader"], f"{groups}, {roles}")
    check("her email address arrived, and is not marked verified",
          token.get("email") == "heidi@acme.test" and token.get("email_verified") is False,
          f"{token.get('email')}, {token.get('email_verified')}")
    status, body = api(heidi["access_token"])
    check("the API gives heidi acme's documents", status == 200 and body.get("tenant") == "acme", f"HTTP {status}")

    ivan = sign_in("ivan")
    groups, _ = groups_and_roles(ivan)
    status, body = api(ivan.get("access_token", "")) if "access_token" in ivan else (0, {})
    tenants = {d.get("tenant") for d in body.get("documents", [])}
    check("ivan is in /globex and sees only globex's documents",
          groups == ["/globex"] and status == 200 and tenants <= {"globex"}, f"{groups}, HTTP {status}")
    judy = sign_in("judy")
    groups, roles = groups_and_roles(judy)
    status, _ = api(judy.get("access_token", "")) if "access_token" in judy else (0, {})
    check("judy has no legacy group: she signs in and the API answers 403",
          groups == [] and roles == [] and status == 403, f"{groups}, HTTP {status}")
    check("a wrong password at the legacy provider gets no token", "access_token" not in sign_in("heidi", "not-her-password"))
    alice = local_sign_in("alice")
    check("DocVault's own alice still signs in with her password",
          "access_token" in alice and claims(alice["access_token"]).get("preferred_username") == "alice")

    # ------------------------------------------------------------------ #
    print("\nWhat goes over the wire")
    request = xml_of(seen, "SAMLRequest")
    response = xml_of(seen)
    _, assertion, _ = parts(response)
    check("DocVault's request is signed", "<dsig:Signature" in request)
    check("the response and the assertion inside it are both signed",
          len(SIGNATURE.findall(response)) == 2 and len(SIGNATURE.findall(assertion)) == 1)
    check("the assertion is addressed to DocVault and nobody else",
          re.findall(r"<saml:Audience>([^<]+)", assertion) == [ENTITY_ID] and f'Destination="{ACS}"' in response)
    name_id = re.search(r"<saml:NameID[^>]*Format=\"([^\"]+)\"[^>]*>([^<]+)", assertion)
    check("the subject is an opaque, persistent identifier, not a name or an address",
          bool(name_id) and name_id.group(1).endswith("nameid-format:persistent")
          and "heidi" not in name_id.group(2), name_id.group(1).rsplit(":", 1)[-1] if name_id else "no NameID")
    unsigned = sign_in("heidi", change_request=lambda f: with_xml(f, SIGNATURE.sub("", xml_of(f, "SAMLRequest")), "SAMLRequest"))
    check("the legacy provider refuses a request that is not signed", "access_token" not in unsigned, describe(unsigned))

    # ------------------------------------------------------------------ #
    print("\nA response changed on its way to DocVault")
    for label, change in (
        ("with every signature removed", no_signatures),
        ("with the assertion's signature removed", no_assertion_signature),
        ("saying another group, signatures left in place", other_group),
        ("saying another person, signatures left in place", other_person),
        ("with a forged assertion in front of the signed one", forged_first),
        ("with a forged assertion wrapped around the signed one", forged_around),
    ):
        result = sign_in("heidi", change_response=change)
        check(f"is refused {label}", "access_token" not in result, describe(result))
    # Stated, not hidden: the assertion's own signature is what DocVault relies on.
    result = sign_in("heidi", change_response=no_envelope_signature)
    check("is accepted with only the outer signature removed: the signed assertion is what counts",
          "access_token" in result and claims(result["access_token"]).get("preferred_username") == f"{ALIAS}.heidi",
          describe(result))

    # ------------------------------------------------------------------ #
    print("\nA real response in the wrong place")
    old = seen["SAMLResponse"]
    result = sign_in("judy", change_response=lambda f: dict(f, SAMLResponse=old))
    check("heidi's earlier response, put into judy's sign-in, is refused", "access_token" not in result, describe(result))
    status, headers, body = http("POST", ACS, {"SAMLResponse": old}, form=True)
    check("the same response posted out of the blue is refused",
          status >= 400 and "code=" not in headers.get("Location", ""), f"HTTP {status} {page_text(body, 60)}")

    # A second service provider at the legacy provider, and a response issued for it.
    other_client = "https://other-service.example"
    try:
        admin("POST", "/clients", {
            "clientId": other_client, "protocol": "saml", "enabled": True, "redirectUris": ["http://localhost:43999/acs"],
            "attributes": {
                "saml_idp_initiated_sso_url_name": "other-service", "saml_assertion_consumer_url_post": "http://localhost:43999/acs",
                "saml.assertion.signature": "true", "saml.server.signature": "true", "saml.client.signature": "false",
            },
        }, realm=LEGACY)
        browser = Browser()
        status, headers, body = browser.request("GET", f"{KEYCLOAK}/realms/{LEGACY}/protocol/saml/clients/other-service")
        for_other = None
        for _ in range(8):
            if status in (302, 303):
                status, headers, body = browser.request("GET", headers["Location"])
                continue
            action, fields = first_form(body)
            if not action or "SAMLResponse" in fields:
                for_other = fields.get("SAMLResponse")
                break
            status, headers, body = browser.request(
                "POST", urllib.parse.urljoin(KEYCLOAK, action), {"username": "heidi", "password": PASSWORD})
        audience = re.findall(r"<saml:Audience>([^<]+)", base64.b64decode(for_other or "").decode())
        result = sign_in("heidi", change_response=lambda f: dict(f, SAMLResponse=for_other))
        check("a signed response the provider issued for another service is refused",
              audience == [other_client] and "access_token" not in result, f"audience {audience}; {describe(result)}")
    finally:
        for found in admin("GET", "/clients?clientId=" + urllib.parse.quote(other_client), realm=LEGACY)[1] or []:
            admin("DELETE", f"/clients/{found['id']}", realm=LEGACY)

    # ------------------------------------------------------------------ #
    print("\nA legacy account cannot take over a DocVault account")
    alice_before = user("alice")
    mail_before = mail_to("alice@acme.test")
    try:
        add_legacy_person("mallory", "alice@acme.test")
        result = sign_in("mallory", OTHER_PASSWORD)
        check("a legacy person carrying alice's email address is refused", "access_token" not in result, describe(result))
        check("nothing is linked to alice, and no DocVault user is made for the newcomer",
              admin("GET", f"/users/{alice_before['id']}/federated-identity")[1] == []
              and user(f"{ALIAS}.mallory") is None)
        mail_after = mail_to("alice@acme.test")
        # Keycloak's built-in flow emails alice a "link your account" confirmation here.
        check("DocVault does not email alice a link to confirm",
              mail_before is not None and mail_after == mail_before, f"{mail_before} before, {mail_after} after")
    finally:
        remove_user("mallory", LEGACY)
        remove_user(f"{ALIAS}.mallory", REALM)
    try:
        add_legacy_person("alice", "alice@legacy.test")
        result = sign_in("alice", OTHER_PASSWORD)
        token = claims(result["access_token"]) if "access_token" in result else {}
        status, _ = api(result.get("access_token", "")) if token else (0, {})
        check("a legacy person named alice is a different DocVault user, with no access",
              token.get("preferred_username") == f"{ALIAS}.alice" and token.get("sub") != alice_before["id"]
              and not token.get("groups") and status == 403, f"{describe(result)}, HTTP {status}")
        alice_after = user("alice")
        check("DocVault's alice is untouched", alice_after["id"] == alice_before["id"]
              and admin("GET", f"/users/{alice_after['id']}/federated-identity")[1] == [])
    finally:
        remove_user("alice", LEGACY)
        remove_user(f"{ALIAS}.alice", REALM)

    # ------------------------------------------------------------------ #
    print("\nA mover and a leaver in the legacy system")
    legacy_heidi = user("heidi", LEGACY)
    legacy_groups = {g["name"]: g["id"] for g in admin("GET", "/groups", realm=LEGACY)[1]}

    def legacy_group(method, name):
        return admin(method, f"/users/{legacy_heidi['id']}/groups/{legacy_groups[name]}", realm=LEGACY)

    session = sign_in("heidi")
    try:
        legacy_group("DELETE", "ACME-ENG")
        legacy_group("PUT", "GLOBEX")
        # Stated, not hidden: DocVault learns of the move only when she signs in again.
        status, refreshed = refresh(session["refresh_token"])
        groups, _ = groups_and_roles(refreshed)
        check("moved to GLOBEX in the legacy system, her running session keeps the old group (stated limit)",
              status == 200 and groups == ["/acme/engineering"], f"HTTP {status}, {groups}")
        moved = sign_in("heidi")
        groups, _ = groups_and_roles(moved)
        check("her next sign-in is in /globex and out of /acme/engineering", groups == ["/globex"], str(groups))
    finally:
        legacy_group("DELETE", "GLOBEX")
        legacy_group("PUT", "ACME-ENG")
    session = sign_in("heidi")
    groups, _ = groups_and_roles(session)
    check("moved back, she is in /acme/engineering again", groups == ["/acme/engineering"], str(groups))
    try:
        admin("PUT", f"/users/{legacy_heidi['id']}", {"enabled": False}, realm=LEGACY)
        result = sign_in("heidi")
        check("disabled in the legacy system, she cannot sign in to DocVault", "access_token" not in result, describe(result))
        status, refreshed = refresh(session["refresh_token"])
        check("her earlier DocVault session still refreshes until it ends (stated limit)",
              status == 200 and "access_token" in refreshed, f"HTTP {status}")
    finally:
        admin("PUT", f"/users/{legacy_heidi['id']}", {"enabled": True}, realm=LEGACY)
    check("enabled again, she signs in", "access_token" in sign_in("heidi"))


def interrupted(signum, _frame):
    # As an exception, so that every `finally` above still puts things back.
    raise SystemExit(f"interrupted by signal {signum}")


if __name__ == "__main__":
    signal.signal(signal.SIGTERM, interrupted)
    try:
        main()
    finally:
        print(f"\n{passed} passed, {failed} failed")
    sys.exit(1 if failed else 0)
