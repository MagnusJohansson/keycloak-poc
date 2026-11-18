#!/usr/bin/env python3
# DocVault - a Keycloak proof-of-concept lab.
# Copyright (C) 2026 Magnus Johansson
# SPDX-License-Identifier: GPL-3.0-or-later
# See the LICENSE file for the full licence text.

"""End-to-end check of LDAP user federation against the REAL stack.

Needs: `make up` (Keycloak, Postgres and the directory), `make seed`, and the
API on :5001. `make assert-ldap` runs it; CI runs it in the realm job.

A sign-in that works proves little here. The settings that matter are the ones
that only show when something changes in the directory: a person leaves a
group, or leaves altogether, and Keycloak goes on issuing the old token. So
this signs in through the real login page as people who exist only in the
directory, then changes the directory under Keycloak and looks again.

It changes the lab's directory and puts it back, also when a check fails. It
stops the directory once, for a few seconds. Do not point it at a directory
you care about.

Standard library only, plus `docker compose` to reach inside the containers.
"""
import base64
import hashlib
import html
import json
import os
import re
import secrets
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

KEYCLOAK = os.environ.get("KEYCLOAK_URL", "http://localhost:8080")
REALM = os.environ.get("KEYCLOAK_REALM", "docvault")
API = os.environ.get("DOCVAULT_API_URL", "http://localhost:5001")
CLIENT = "docvault-web-react"
REDIRECT = "http://localhost:5173/callback"  # nothing needs to listen
PASSWORD = os.environ.get("SEED_USER_PASSWORD", "DocVaultLab!2026")
TOKEN_URL = f"{KEYCLOAK}/realms/{REALM}/protocol/openid-connect/token"
# The local lab's admin (infra/environments/local/realm.tfvars).
ADMIN_USER = os.environ.get("KEYCLOAK_ADMIN_USER", "admin")
ADMIN_PASSWORD = os.environ.get("KEYCLOAK_ADMIN_PASSWORD", "admin")
COMPOSE_FILE = os.environ.get(
    "COMPOSE_FILE", os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "infra", "local", "docker-compose.yml"))

BASE = "dc=docvault,dc=test"
PEOPLE = f"ou=people,{BASE}"
BIND_DN = f"cn=keycloak,ou=services,{BASE}"
BIND_PASSWORD = os.environ.get("LDAP_BIND_PASSWORD", "keycloak-bind-lab")
# Root inside the directory's container, over its local socket, is its
# administrator. There is no admin password (infra/local/ldap/slapd.conf).
SOCKET = "ldapi://%2Frun%2Fopenldap%2Fldapi"
AS_ADMIN = f"-Q -Y EXTERNAL -H {SOCKET}"
AS_KEYCLOAK = f"-x -H ldaps://localhost -o TLS_REQCERT=never -D {BIND_DN} -w {BIND_PASSWORD}"

passed = failed = 0


def check(label, ok, detail=""):
    global passed, failed
    passed, failed = passed + bool(ok), failed + (not ok)
    print(f"  {'PASS' if ok else 'FAIL'}  {label}" + (f"  [{detail}]" if detail else ""))


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


def http(method, url, data=None, headers=None, form=False, cookies=None):
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
    if cookies:
        headers["Cookie"] = "; ".join(f"{k}={v}" for k, v in cookies.items())
    request = urllib.request.Request(url, data=body, headers=headers, method=method)
    try:
        response = urllib.request.build_opener(NoRedirect()).open(request, timeout=60)
    except urllib.error.HTTPError as error:
        response = error
    if cookies is not None:
        # By hand: http.cookiejar drops Keycloak's cookies on plain-HTTP localhost.
        for line in response.headers.get_all("Set-Cookie") or []:
            name, _, value = line.split(";", 1)[0].partition("=")
            if value:
                cookies[name.strip()] = value
            else:
                cookies.pop(name.strip(), None)
    return response.status, response.headers, response.read().decode()


def claims(jwt):
    payload = jwt.split(".")[1]
    return json.loads(base64.urlsafe_b64decode(payload + "=" * (-len(payload) % 4)))


def sign_in(username, password=PASSWORD):
    """Authorization code + PKCE through the real login page.

    Returns the token endpoint's JSON, or {"error": ...} if the page refused.
    """
    cookies = {}
    verifier = secrets.token_urlsafe(48)
    challenge = base64.urlsafe_b64encode(hashlib.sha256(verifier.encode()).digest()).rstrip(b"=").decode()
    query = {
        "client_id": CLIENT, "response_type": "code", "redirect_uri": REDIRECT, "scope": "openid profile email",
        "code_challenge": challenge, "code_challenge_method": "S256", "state": "s",
    }
    status, headers, body = http(
        "GET", f"{KEYCLOAK}/realms/{REALM}/protocol/openid-connect/auth?" + urllib.parse.urlencode(query),
        cookies=cookies)
    if status == 200:
        form = re.search(r'<form[^>]*action="([^"]+)"', body)
        if not form:
            return {"error": "no_login_form"}
        status, headers, body = http(
            "POST", html.unescape(form.group(1)), {"username": username, "password": password},
            form=True, cookies=cookies)
    for _ in range(6):
        if status in (302, 303) and headers["Location"].startswith(KEYCLOAK):
            status, headers, body = http("GET", headers["Location"], cookies=cookies)
        else:
            break
    if status not in (302, 303):
        return {"error": "login_refused", "status": status}
    params = urllib.parse.parse_qs(urllib.parse.urlparse(headers["Location"]).query)
    if "code" not in params:
        return {"error": params.get("error", ["no_code"])[0]}
    _, _, body = http("POST", TOKEN_URL, {
        "grant_type": "authorization_code", "client_id": CLIENT, "code": params["code"][0],
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
        return "a token was issued"
    return json.dumps({k: v for k, v in response.items() if k in ("error", "error_description", "status")})[:160]


def groups_and_roles(response):
    if "access_token" not in response:
        return None, None
    token = claims(response["access_token"])
    return token.get("groups") or [], sorted(token.get("resource_access", {}).get("docvault-api", {}).get("roles", []))


def api(token, path="/documents"):
    status, _, body = http("GET", API + path, headers={"Authorization": f"Bearer {token}"})
    try:
        return status, json.loads(body) if body else {}
    except json.JSONDecodeError:
        return status, {}


def admin_headers():
    _, _, body = http("POST", f"{KEYCLOAK}/realms/master/protocol/openid-connect/token", {
        "grant_type": "password", "client_id": "admin-cli", "username": ADMIN_USER, "password": ADMIN_PASSWORD,
    }, form=True)
    return {"Authorization": "Bearer " + json.loads(body)["access_token"]}


def admin(method, path, data=None):
    status, _, body = http(method, f"{KEYCLOAK}/admin/realms/{REALM}{path}", data, headers=admin_headers())
    try:
        return status, json.loads(body) if body else None
    except json.JSONDecodeError:
        return status, body


def keycloak_user(username):
    _, users = admin("GET", "/users?exact=true&username=" + urllib.parse.quote(username))
    return users[0] if users else None


def compose(*args, stdin=None):
    result = subprocess.run(
        ["docker", "compose", "-f", COMPOSE_FILE, *args], input=stdin, capture_output=True, text=True, timeout=120)
    return result.returncode, result.stdout + result.stderr


def directory(command, stdin=None):
    """Run an LDAP client command inside the directory's container."""
    return compose("exec", "-T", "ldap", "sh", "-c", command, stdin=stdin)


def person_ldif(uid, mail, password):
    return (f"dn: uid={uid},{PEOPLE}\nobjectClass: inetOrgPerson\nuid: {uid}\ngivenName: Test\nsn: Person\n"
            f"cn: Test Person\nmail: {mail}\nuserPassword: {password}\n")


def set_engineering(member_uid):
    return directory(f"ldapmodify {AS_ADMIN}", (
        f"dn: cn=engineering,ou=groups,{BASE}\nchangetype: modify\nreplace: member\n"
        f"member: uid={member_uid},{PEOPLE}\n"))


def wait_for_directory():
    for _ in range(30):
        if directory(f"ldapwhoami {AS_ADMIN}")[0] == 0:
            return True
        time.sleep(1)
    return False


def main():
    code, out = directory(f"ldapwhoami {AS_ADMIN}")
    if code != 0:
        sys.exit(f"The directory is not running (make up): {out.strip()[:200]}")
    _, components = admin("GET", "/components?type=org.keycloak.storage.UserStorageProvider")
    if not components:
        sys.exit("The realm has no LDAP federation (make seed).")
    federation = components[0]
    config = {k: (v[0] if v else None) for k, v in federation["config"].items()}

    # ------------------------------------------------------------------ #
    print("\nThe directory keeps its own doors shut")
    code, out = directory(f"ldapsearch -x -H ldaps://localhost -o TLS_REQCERT=never -b {BASE} '(uid=erin)' dn")
    check("an anonymous search is refused", code != 0 and "uid=erin" not in out, out.strip()[:80])

    code, out = directory(f"ldapsearch -LLL {AS_KEYCLOAK} -b {BASE} '(uid=erin)' mail userPassword")
    check("Keycloak's account can read a person", code == 0 and "mail: erin@acme.test" in out)
    check("Keycloak's account cannot read a password", "userPassword" not in out)

    code, out = directory(
        f"ldapmodify {AS_KEYCLOAK}", f"dn: uid=erin,{PEOPLE}\nchangetype: modify\nreplace: sn\nsn: Changed\n")
    check("Keycloak's account cannot write", code != 0 and "Insufficient access" in out, out.strip()[-60:])

    code, out = directory(f"ldapwhoami -x -H ldap://localhost -D {BIND_DN} -w {BIND_PASSWORD}")
    check("the directory does not listen without TLS", code != 0, out.strip()[:80])

    code, out = directory(f"ldapsearch -LLL {AS_ADMIN} -o ldif-wrap=no -b {PEOPLE} '(uid=erin)' userPassword")
    stored = re.search(r"userPassword:: (\S+)", out)
    stored = base64.b64decode(stored.group(1)).decode() if stored else ""
    check("the directory stores a password as an Argon2 hash", stored.startswith("{ARGON2}"), stored[:9])

    # ------------------------------------------------------------------ #
    print("\nKeycloak's connection to the directory")
    check("the federation is read-only", config.get("editMode") == "READ_ONLY", str(config.get("editMode")))
    check("it uses TLS", str(config.get("connectionUrl", "")).startswith("ldaps://"), str(config.get("connectionUrl")))
    check("it does not cache people", config.get("cachePolicy") == "NO_CACHE", str(config.get("cachePolicy")))
    check("the admin API does not give out the bind password",
          config.get("bindCredential") not in (None, BIND_PASSWORD) and set(config["bindCredential"]) == {"*"})

    def test_connection(url):
        status, answer = admin("POST", "/testLDAPConnection", {
            "action": "testAuthentication", "connectionUrl": url, "bindDn": BIND_DN,
            "bindCredential": "**********", "componentId": federation["id"], "authType": "simple",
            "useTruststoreSpi": "always", "startTls": "false", "connectionTimeout": "5000",
        })
        return status, (answer or {}).get("errorMessage") if isinstance(answer, dict) else None

    check("Keycloak can bind to the directory", test_connection(config["connectionUrl"])[0] == 204)
    # The same server under a second name (docker-compose.yml), which its
    # certificate does not carry. The name resolves, so only TLS can refuse it.
    status, error = test_connection("ldaps://ldap-under-another-name:636")
    check("Keycloak refuses the directory under a name its certificate lacks",
          status == 400 and error == "SSLHandshakeFailed", f"HTTP {status} {error}")

    # ------------------------------------------------------------------ #
    print("\nPeople from the directory sign in")
    erin = sign_in("erin")
    check("erin signs in with her directory password", "access_token" in erin, describe(erin))
    if "access_token" not in erin:
        return
    token = claims(erin["access_token"])
    groups, roles = groups_and_roles(erin)
    check("her group is the realm's /acme/engineering", groups == ["/acme/engineering"], str(groups))
    check("her roles come from that group, in the realm", roles == ["doc.editor", "doc.reader"], str(roles))
    check("her first name is the directory's givenName, not its cn",
          token.get("given_name") == "Erin" and token.get("family_name") == "Eriksson",
          f"{token.get('given_name')} / {token.get('family_name')}")
    status, body = api(erin["access_token"])
    check("the API gives erin acme's documents", status == 200 and body.get("tenant") == "acme", f"HTTP {status}")

    frank = sign_in("frank")
    groups, _ = groups_and_roles(frank)
    status, body = api(frank.get("access_token", "")) if "access_token" in frank else (0, {})
    tenants = {d.get("tenant") for d in body.get("documents", [])}
    check("frank is in /globex and sees only globex's documents",
          groups == ["/globex"] and status == 200 and tenants <= {"globex"}, f"{groups}, HTTP {status}, {sorted(tenants)}")

    grace = sign_in("grace")
    groups, roles = groups_and_roles(grace)
    status, _ = api(grace.get("access_token", "")) if "access_token" in grace else (0, {})
    check("grace is in no group: she signs in and the API answers 403",
          groups == [] and roles == [] and status == 403, f"{groups}, {roles}, HTTP {status}")

    check("a wrong password is refused", "access_token" not in sign_in("erin", "not-her-password"))
    check("Keycloak's own bind account is not a user", "access_token" not in sign_in("keycloak", BIND_PASSWORD))
    wildcards = [name for name in ("*", "er*", "erin)(uid=*", "*)(uid=erin") if "access_token" in sign_in(name)]
    check("a wildcard or a filter in the username signs nobody in", not wildcards, str(wildcards))

    # ------------------------------------------------------------------ #
    print("\nKeycloak holds a link to the person, not her password")
    user = keycloak_user("erin")
    check("erin's Keycloak user points at her directory entry",
          user and user.get("federationLink") == federation["id"]
          and user.get("attributes", {}).get("LDAP_ENTRY_DN") == [f"uid=erin,{PEOPLE}"])
    code, out = compose("exec", "-T", "postgres", "psql", "-U", "keycloak", "-d", "keycloak", "-At", "-c",
                        f"select count(*) from credential where user_id = '{user['id']}'")
    check("Keycloak's database has no credential for her", code == 0 and out.strip() == "0", out.strip()[:40])

    status, _ = admin("PUT", f"/users/{user['id']}", {"firstName": "Changed"})
    check("Keycloak refuses to change her name", status == 400 and keycloak_user("erin")["firstName"] == "Erin", f"HTTP {status}")
    status, _ = admin("PUT", f"/users/{user['id']}/reset-password",
                      {"type": "password", "value": "Another!Password2026", "temporary": False})
    check("Keycloak refuses to set her password", status == 400, f"HTTP {status}")
    check("and the refused password does not work", "access_token" not in sign_in("erin", "Another!Password2026"))

    # ------------------------------------------------------------------ #
    print("\nA directory entry cannot take over a local account")
    alice_before = keycloak_user("alice")
    try:
        directory(f"ldapadd {AS_ADMIN}", person_ldif("alice", "another-alice@acme.test", "Directory!Password2026"))
        check("a directory 'alice' cannot sign in as the realm's alice",
              "access_token" not in sign_in("alice", "Directory!Password2026"))
        alice = sign_in("alice")
        alice_after = keycloak_user("alice")
        check("the realm's alice still signs in, and is still a local user",
              "access_token" in alice and alice_after["id"] == alice_before["id"]
              and not alice_after.get("federationLink"), describe(alice))
    finally:
        directory(f"ldapdelete {AS_ADMIN} uid=alice,{PEOPLE}")
    try:
        directory(f"ldapadd {AS_ADMIN}", person_ldif("mallory", "alice@acme.test", "Directory!Password2026"))
        check("a directory entry with alice's email address is refused at sign-in",
              "access_token" not in sign_in("mallory", "Directory!Password2026")
              and "access_token" not in sign_in("alice@acme.test", "Directory!Password2026")
              and keycloak_user("mallory") is None)
    finally:
        directory(f"ldapdelete {AS_ADMIN} uid=mallory,{PEOPLE}")

    # ------------------------------------------------------------------ #
    print("\nA mover: erin leaves engineering")
    session = sign_in("erin")
    try:
        set_engineering("nobody")  # a group needs a member; this one matches no person
        status, refreshed = refresh(session["refresh_token"])
        groups, roles = groups_and_roles(refreshed)
        check("her next refreshed token has lost the group and its roles",
              status == 200 and groups == [] and roles == [], f"HTTP {status}, {groups}, {roles}")
        status, _ = api(refreshed.get("access_token", "")) if "access_token" in refreshed else (0, {})
        check("the API refuses that token", status == 403, f"HTTP {status}")
        # Stated, not hidden: the token issued before the change is still valid.
        status, _ = api(session["access_token"])
        check("the token from before the change works until it expires (5 minutes)", status == 200, f"HTTP {status}")
    finally:
        set_engineering("erin")
    groups, _ = groups_and_roles(sign_in("erin"))
    check("back in the group, she has it again", groups == ["/acme/engineering"], str(groups))

    # ------------------------------------------------------------------ #
    print("\nA leaver: erin is deleted from the directory")
    _, saved = directory(f"ldapsearch -LLL {AS_ADMIN} -o ldif-wrap=no -b uid=erin,{PEOPLE} -s base '*' entryUUID")
    session = sign_in("erin")
    id_before = keycloak_user("erin")["id"]
    try:
        directory(f"ldapdelete {AS_ADMIN} uid=erin,{PEOPLE}")
        status, refreshed = refresh(session["refresh_token"])
        check("her session cannot be refreshed", status == 400 and "access_token" not in refreshed, f"HTTP {status}")
        check("she cannot sign in", "access_token" not in sign_in("erin"))
        check("Keycloak has dropped its copy of her", keycloak_user("erin") is None)
    finally:
        # -e relax: lets the entry keep its entryUUID, as a restore from backup would.
        directory(f"ldapadd {AS_ADMIN} -e relax", saved)
    again = sign_in("erin")
    groups, _ = groups_and_roles(again)
    check("restored to the directory, she signs in again", groups == ["/acme/engineering"], describe(again))
    # Not a pass or a fail, but worth knowing: to Keycloak she is a new person.
    id_after = (keycloak_user("erin") or {}).get("id")
    print(f"        note: her Keycloak id (the token's `sub`) "
          f"{'changed' if id_after != id_before else 'did not change'} across the delete and restore")

    # ------------------------------------------------------------------ #
    print("\nThe directory is down")
    session = sign_in("erin")
    try:
        compose("stop", "ldap")
        check("a directory person cannot sign in", "access_token" not in sign_in("erin"))
        status, refreshed = refresh(session["refresh_token"])
        check("or refresh a session", status != 200 and "access_token" not in refreshed, f"HTTP {status}")
        alice = sign_in("alice")
        check("a local user is not affected", "access_token" in alice, describe(alice))
    finally:
        compose("start", "ldap")
        wait_for_directory()
    # Recovery is not always immediate: in roughly one local run in five the
    # first sign-in after the directory returned still failed, and it took about
    # ten seconds more. Why is not established. The check waits and says how long.
    started = time.time()
    groups, _ = groups_and_roles(sign_in("erin"))
    while groups != ["/acme/engineering"] and time.time() - started < 30:
        time.sleep(2)
        groups, _ = groups_and_roles(sign_in("erin"))
    check("with the directory back, erin signs in again", groups == ["/acme/engineering"],
          f"{groups}, after {time.time() - started:.0f}s")


if __name__ == "__main__":
    try:
        main()
    finally:
        print(f"\n{passed} passed, {failed} failed")
    sys.exit(1 if failed else 0)
