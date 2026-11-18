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

It changes the lab's directory and puts it back, also when a check fails or
the run is interrupted. It freezes the directory once, for a few seconds. Do
not point it at a directory you care about. If a run is killed outright,
`docker compose up -d --force-recreate ldap` resets the directory to its seed.

Standard library only, plus `docker compose` to reach inside the containers.
"""
import base64
import hashlib
import html
import json
import os
import re
import secrets
import signal
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


def forgive():
    """Clear the realm's brute-force counters.

    The realm locks an account for a minute after two failed sign-ins within a
    second. This script fails sign-ins on purpose, in quick succession, so it
    clears the counters before a sign-in that is meant to succeed.
    """
    admin("DELETE", "/attack-detection/brute-force/users")


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


def restore(what, command, stdin=None):
    """A clean-up step in the directory. A failure here is said out loud."""
    code, out = directory(command, stdin)
    if code != 0:
        print(f"  WARNING  could not {what}: {out.strip()[:120]}. Reset the directory with "
              f"`docker compose -f infra/local/docker-compose.yml up -d --force-recreate ldap`.")
    return code == 0


def group_ldif(cn, member_uid):
    return (f"dn: cn={cn},ou=groups,{BASE}\nobjectClass: groupOfNames\ncn: {cn}\n"
            f"member: uid={member_uid},{PEOPLE}\n")


def realm_group_paths():
    paths = []

    def walk(groups):
        for group in groups or []:
            paths.append(group["path"])
            walk(admin("GET", f"/groups/{group['id']}/children")[1])

    walk(admin("GET", "/groups")[1])
    return sorted(paths)


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
    compose("unpause", "ldap")  # left frozen if an earlier run was killed mid-outage; harmless otherwise
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
    if code == 0:
        restore("undo that write", f"ldapmodify {AS_ADMIN}",
                f"dn: uid=erin,{PEOPLE}\nchangetype: modify\nreplace: sn\nsn: Eriksson\n")

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
            "useTruststoreSpi": config.get("useTruststoreSpi") or "ldapsOnly", "startTls": "false",
            "connectionTimeout": "5000",
        })
        return status, (answer or {}).get("errorMessage") if isinstance(answer, dict) else None

    check("Keycloak can bind to the directory", test_connection(config["connectionUrl"])[0] == 204)
    # The same server under a second name (docker-compose.yml), which its
    # certificate does not carry. The name resolves, so only TLS can refuse it.
    status, error = test_connection("ldaps://ldap-under-another-name:636")
    check("Keycloak refuses the directory under a name its certificate lacks",
          status == 400 and error == "SSLHandshakeFailed", f"HTTP {status} {error}")

    # Keycloak trusts this certificate file. Were it a CA, whoever held its key
    # could sign a certificate for any name and Keycloak would accept that too.
    code, out = directory("openssl x509 -in /tls/public/ldap.crt -noout -ext basicConstraints")
    check("the directory's certificate is not a CA", code == 0 and "CA:FALSE" in out, out.strip()[-40:])

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

    # The realm's lockout (realm.tf) is Keycloak's, so it covers directory people.
    # The lock needs two failures within a second. A slow machine may need a few
    # more tries to land two that close; it stays well under the ten that lock
    # an account outright.
    forgive()
    grace_id = keycloak_user("grace")["id"]
    state = {}
    for attempt in range(1, 7):
        sign_in("grace", f"wrong-{attempt}")
        state = admin("GET", f"/attack-detection/brute-force/users/{grace_id}")[1]
        if attempt > 1 and state.get("disabled"):
            break
    locked = sign_in("grace")
    check("quick wrong passwords lock a directory person out, even with the right one after",
          "access_token" not in locked and state.get("disabled") is True, f"{state.get('numFailures')} failures")
    forgive()

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
    forgive()

    # ------------------------------------------------------------------ #
    print("\nA directory entry cannot take over a local account")
    alice_before = keycloak_user("alice")
    try:
        directory(f"ldapadd {AS_ADMIN}", person_ldif("alice", "another-alice@acme.test", "Directory!Password2026"))
        check("a directory 'alice' cannot sign in as the realm's alice",
              "access_token" not in sign_in("alice", "Directory!Password2026"))
        forgive()
        alice = sign_in("alice")
        alice_after = keycloak_user("alice")
        check("the realm's alice still signs in, and is still a local user",
              "access_token" in alice and alice_after["id"] == alice_before["id"]
              and not alice_after.get("federationLink"), describe(alice))
    finally:
        restore("remove the directory's alice", f"ldapdelete {AS_ADMIN} uid=alice,{PEOPLE}")
    try:
        directory(f"ldapadd {AS_ADMIN}", person_ldif("mallory", "alice@acme.test", "Directory!Password2026"))
        check("a directory entry with alice's email address is refused at sign-in",
              "access_token" not in sign_in("mallory", "Directory!Password2026")
              and "access_token" not in sign_in("alice@acme.test", "Directory!Password2026")
              and keycloak_user("mallory") is None)
    finally:
        restore("remove mallory", f"ldapdelete {AS_ADMIN} uid=mallory,{PEOPLE}")
    forgive()
    # The refused import leaves something behind in Keycloak's user cache: on a
    # new Keycloak, "mallory" then could not sign in at all, even as a fresh and
    # valid directory entry, and Keycloak did not ask the directory. Clearing the
    # user cache (Realm settings, Cache, in the console) is what let her in.
    admin("POST", "/clear-user-cache")

    # The same two moves by someone Keycloak already knows. Keycloak takes a
    # username and an email address once, when it first meets a person; were it
    # to re-read them, mallory's token would carry alice's address, verified.
    mallory_dn = f"uid=mallory,{PEOPLE}"
    try:
        directory(f"ldapadd {AS_ADMIN}", person_ldif("mallory", "mallory@acme.test", "Directory!Password2026"))
        session = sign_in("mallory", "Directory!Password2026")
        directory(f"ldapmodify {AS_ADMIN}",
                  f"dn: {mallory_dn}\nchangetype: modify\nreplace: mail\nmail: alice@acme.test\n")
        _, refreshed = refresh(session.get("refresh_token", ""))
        again = sign_in("mallory", "Directory!Password2026")
        emails = [claims(r["access_token"]).get("email") for r in (refreshed, again) if "access_token" in r]
        check("a known directory person who takes alice's email address does not get it in her token",
              emails == ["mallory@acme.test"] * 2, f"{emails} {describe(session)} / {describe(again)}")
        directory(f"ldapmodrdn {AS_ADMIN} -r {mallory_dn} uid=alice")
        mallory_dn = f"uid=alice,{PEOPLE}"
        _, refreshed = refresh(again.get("refresh_token", ""))
        names = [claims(r["access_token"]).get("preferred_username") for r in (refreshed,) if "access_token" in r]
        check("renamed to 'alice' in the directory, she is still 'mallory' in her token",
              names == ["mallory"], str(names))
        check("and she still cannot sign in as alice",
              "access_token" not in sign_in("alice", "Directory!Password2026"))
        forgive()
        named_alice = admin("GET", "/users?exact=true&username=alice")[1]
        check("the realm still has exactly one alice, the local one",
              len(named_alice) == 1 and named_alice[0]["id"] == alice_before["id"], str(len(named_alice)))
    finally:
        restore("remove mallory", f"ldapdelete {AS_ADMIN} {mallory_dn}")
    # Asking Keycloak for her by name makes it look in the directory, find her
    # gone, and drop its own copy. Without this each run left a Keycloak user
    # "mallory" behind, and the next run behaved differently from a first one.
    keycloak_user("mallory")

    # ------------------------------------------------------------------ #
    print("\nA directory group is only what the realm says it is")
    groups_before = realm_group_paths()
    try:
        directory(f"ldapadd {AS_ADMIN}", group_ldif("contractors", "erin"))
        directory(f"ldapadd {AS_ADMIN}", group_ldif("legal", "grace"))
        groups, _ = groups_and_roles(sign_in("erin"))
        check("a directory group the realm does not list adds nothing to erin's token",
              groups == ["/acme/engineering"], str(groups))
        check("and creates no group in the realm", realm_group_paths() == groups_before, str(realm_group_paths()))
        groups, roles = groups_and_roles(sign_in("grace"))
        check("a directory group named like the realm's /acme/legal does not put grace in it",
              groups == [] and roles == [], f"{groups}, {roles}")
        # Two more realm groups named 'engineering': one at the top, one under
        # /globex. Matched by name alone, the top-level one took erin out of Acme.
        decoys = []
        globex = [g for g in admin("GET", "/groups")[1] if g["path"] == "/globex"][0]
        try:
            admin("POST", "/groups", {"name": "engineering"})
            admin("POST", f"/groups/{globex['id']}/children", {"name": "engineering"})
            decoys = ([g["id"] for g in admin("GET", "/groups")[1] if g["path"] == "/engineering"]
                      + [g["id"] for g in admin("GET", f"/groups/{globex['id']}/children")[1] or []
                         if g["name"] == "engineering"])
            groups, _ = groups_and_roles(sign_in("erin"))
            check("other realm groups named 'engineering' do not capture erin",
                  len(decoys) == 2 and groups == ["/acme/engineering"], f"{len(decoys)} decoys, {groups}")
        finally:
            for group_id in decoys:
                admin("DELETE", f"/groups/{group_id}")
        # An administrator's "sync groups" button, with the directory's nesting
        # taken apart first: engineering no longer a member of acme. Nothing in
        # the realm may move.
        acme_dn = f"cn=acme,ou=groups,{BASE}"
        try:
            directory(f"ldapmodify {AS_ADMIN}",
                      f"dn: {acme_dn}\nchangetype: modify\nreplace: member\nmember: uid=nobody,{PEOPLE}\n")
            mappers = [m for m in admin("GET", f"/components?parent={federation['id']}")[1]
                       if m.get("providerId") == "group-ldap-mapper"]
            statuses = [admin("POST", f"/user-storage/{federation['id']}/mappers/{m['id']}/sync?direction=fedToKeycloak")[0]
                        for m in mappers]
            groups, _ = groups_and_roles(sign_in("erin"))
            check("a group sync, with the directory's nesting taken apart, changes nothing in the realm",
                  len(mappers) >= 1 and set(statuses) == {200} and realm_group_paths() == groups_before
                  and groups == ["/acme/engineering"], f"{statuses}, {groups}")
        finally:
            restore("put engineering back under acme", f"ldapmodify {AS_ADMIN}", (
                f"dn: {acme_dn}\nchangetype: modify\nreplace: member\n"
                f"member: cn=engineering,ou=groups,{BASE}\n"))
    finally:
        restore("remove the test groups", f"ldapdelete {AS_ADMIN} cn=contractors,ou=groups,{BASE} cn=legal,ou=groups,{BASE}")

    # ------------------------------------------------------------------ #
    forgive()
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
        if set_engineering("erin")[0] != 0:
            print("  WARNING  could not put erin back in engineering; recreate the ldap container")
    groups, _ = groups_and_roles(sign_in("erin"))
    check("back in the group, she has it again", groups == ["/acme/engineering"], str(groups))

    # ------------------------------------------------------------------ #
    print("\nA leaver: erin is deleted from the directory")
    code, saved = directory(f"ldapsearch -LLL {AS_ADMIN} -o ldif-wrap=no -b uid=erin,{PEOPLE} -s base '*' entryUUID")
    if code != 0 or f"dn: uid=erin,{PEOPLE}" not in saved or "userPassword" not in saved:
        check("erin's entry could be saved before deleting it", False, saved.strip()[:80])
        return
    session = sign_in("erin")
    id_before = keycloak_user("erin")["id"]
    try:
        directory(f"ldapdelete {AS_ADMIN} uid=erin,{PEOPLE}")
        status, refreshed = refresh(session["refresh_token"])
        check("her session cannot be refreshed", status == 400 and "access_token" not in refreshed, f"HTTP {status}")
        check("she cannot sign in", "access_token" not in sign_in("erin"))
        check("Keycloak has dropped its copy of her", keycloak_user("erin") is None)
        forgive()
    finally:
        # -e relax: lets the entry keep its entryUUID, as a restore from backup would.
        restore("put erin back", f"ldapadd {AS_ADMIN} -e relax", saved)
    again = sign_in("erin")
    groups, _ = groups_and_roles(again)
    check("restored to the directory, she signs in again", groups == ["/acme/engineering"], describe(again))
    # Not a pass or a fail, but worth knowing: to Keycloak she is a new person.
    id_after = (keycloak_user("erin") or {}).get("id")
    print(f"        note: her Keycloak id (the token's `sub`) "
          f"{'changed' if id_after != id_before else 'did not change'} across the delete and restore")

    # ------------------------------------------------------------------ #
    # Frozen, not stopped: stopping the container also removes its name from
    # Docker's DNS, and Java then caches the failed lookup for ten seconds, so a
    # sign-in can go on failing after the directory is back. That is a property
    # of this lab's outage, not of Keycloak's recovery. A frozen directory keeps
    # its name and simply does not answer, which is what an outage looks like.
    print("\nThe directory does not answer")
    session = sign_in("erin")
    try:
        compose("pause", "ldap")
        check("a directory person cannot sign in", "access_token" not in sign_in("erin"))
        status, refreshed = refresh(session["refresh_token"])
        check("or refresh a session", status != 200 and "access_token" not in refreshed, f"HTTP {status}")
        alice = sign_in("alice")
        check("a local user is not affected", "access_token" in alice, describe(alice))
    finally:
        compose("unpause", "ldap")  # first: nothing after it may leave the directory frozen
        if not wait_for_directory():
            print("  WARNING  the directory did not come back; recreate the ldap container")
        forgive()
    started = time.time()
    groups, _ = groups_and_roles(sign_in("erin"))
    check("with the directory back, erin signs in again at once", groups == ["/acme/engineering"],
          f"{groups}, after {time.time() - started:.1f}s")


def interrupted(signum, _frame):
    # As an exception, so that every `finally` above still puts the directory back.
    raise SystemExit(f"interrupted by signal {signum}")


if __name__ == "__main__":
    signal.signal(signal.SIGTERM, interrupted)
    try:
        main()
    finally:
        print(f"\n{passed} passed, {failed} failed")
    sys.exit(1 if failed else 0)
