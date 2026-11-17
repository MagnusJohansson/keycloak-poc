#!/usr/bin/env python3
# DocVault - a Keycloak proof-of-concept lab.
# Copyright (C) 2026 Magnus Johansson
# SPDX-License-Identifier: GPL-3.0-or-later
# See the LICENSE file for the full licence text.

"""End-to-end check of MCP authorization against the REAL stack.

Needs: Keycloak up and seeded, the API on :5001, the MCP server on :5002, and
MCP_CLIENT_SECRET (terraform output mcp_server_client_secret). `make assert-mcp`
sets all of that up; CI runs it in the realm job.

The unit tests in apps/mcp-dotnet prove the server refuses forged tokens. They
cannot prove that Keycloak issues the tokens the server expects: every claim
here depends on realm configuration that fails silently when wrong (a missing
mapper, a feature flag that is off). So this signs in as the demo users through
the real login page, the way an MCP client's browser step does, and asserts on
what comes back.

Standard library only, so it runs on a bare CI runner.
"""
import base64
import hashlib
import html
import json
import os
import re
import secrets
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

KEYCLOAK = os.environ.get("KEYCLOAK_URL", "http://localhost:8080")
REALM = os.environ.get("KEYCLOAK_REALM", "docvault")
MCP = os.environ.get("MCP_RESOURCE_URL", "http://localhost:5002/mcp")
API = os.environ.get("DOCVAULT_API_URL", "http://localhost:5001")
CLIENT = "docvault-mcp-client"
PASSWORD = os.environ.get("SEED_USER_PASSWORD", "DocVaultLab!2026")
SECRET = os.environ.get("MCP_CLIENT_SECRET", "")
REDIRECT = "http://localhost:43210/callback"  # any loopback port; nothing listens
TOKEN_URL = f"{KEYCLOAK}/realms/{REALM}/protocol/openid-connect/token"
# The MCP server's audit file (its Audit:File) and the API's log. `make mcp`,
# `make api` and CI write both; without them the audit checks are skipped.
AUDIT_LOG = os.environ.get("MCP_AUDIT_LOG", "")
API_LOG = os.environ.get("DOCVAULT_API_LOG", "")
# The local lab's admin (infra/environments/local/realm.tfvars), to read events.
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
        response = urllib.request.build_opener(NoRedirect()).open(request, timeout=30)
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


def page_text(body, limit=200):
    """What a Keycloak HTML page says, for a failure message. Pages carry no tokens."""
    text = re.sub(r"<script.*?</script>|<style.*?</style>|<[^>]+>", " ", body, flags=re.S)
    return re.sub(r"\s+", " ", html.unescape(text)).strip()[:limit]


def claims(jwt):
    payload = jwt.split(".")[1]
    return json.loads(base64.urlsafe_b64decode(payload + "=" * (-len(payload) % 4)))


# The text of each consent screen Keycloak showed, in order. It shows one only
# while the user has not yet granted what is asked, so on a realm that has been
# signed in to before there may be none.
consent_screens = []


def sign_in(username, scope, resource=MCP):
    """Authorization code + PKCE through the real login and consent pages.

    Returns the token endpoint's JSON (an error object if the flow was refused).
    """
    cookies = {}
    verifier = secrets.token_urlsafe(48)
    challenge = base64.urlsafe_b64encode(hashlib.sha256(verifier.encode()).digest()).rstrip(b"=").decode()
    query = {
        "client_id": CLIENT, "response_type": "code", "redirect_uri": REDIRECT, "scope": scope,
        "code_challenge": challenge, "code_challenge_method": "S256", "state": "s",
    }
    if resource:
        query["resource"] = resource
    status, headers, body = http(
        "GET", f"{KEYCLOAK}/realms/{REALM}/protocol/openid-connect/auth?" + urllib.parse.urlencode(query),
        cookies=cookies)
    if status == 200:
        form = re.search(r'<form[^>]*action="([^"]+)"', body)
        if not form:
            return {"error": "no_login_form"}
        status, headers, body = http(
            "POST", html.unescape(form.group(1)), {"username": username, "password": PASSWORD},
            form=True, cookies=cookies)
    for _ in range(6):  # consent and other intermediate Keycloak pages
        if status in (302, 303) and headers["Location"].startswith(KEYCLOAK):
            status, headers, body = http("GET", headers["Location"], cookies=cookies)
        elif status == 200 and 'name="accept"' in body:
            consent_screens.append(page_text(body, limit=2000))
            form = re.search(r'<form[^>]*action="([^"]+)"', body)
            status, headers, body = http(
                "POST", urllib.parse.urljoin(KEYCLOAK, html.unescape(form.group(1))), {"accept": "Yes"},
                form=True, cookies=cookies)
        else:
            break
    if status not in (302, 303):
        return {"error": "login_failed", "status": status, "error_description": page_text(body)}
    params = urllib.parse.parse_qs(urllib.parse.urlparse(headers["Location"]).query)
    if "code" not in params:
        return {"error": params.get("error", ["no_code"])[0], "iss": params.get("iss", [None])[0]}
    token_request = {
        "grant_type": "authorization_code", "client_id": CLIENT, "code": params["code"][0],
        "redirect_uri": REDIRECT, "code_verifier": verifier,
    }
    if resource:
        token_request["resource"] = resource
    _, _, body = http("POST", TOKEN_URL, token_request, form=True)
    result = json.loads(body)
    result["iss_param"] = params.get("iss", [None])[0]
    return result


def describe(response):
    """An error response for a log line. Never a token, not even part of one."""
    if "access_token" in response:
        return "a token was issued"
    return json.dumps({k: v for k, v in response.items() if k in ("error", "error_description", "status")})[:160]


def exchange(subject_token, client_id, client_secret):
    status, _, body = http("POST", TOKEN_URL, {
        "grant_type": "urn:ietf:params:oauth:grant-type:token-exchange",
        "client_id": client_id, "client_secret": client_secret,
        "subject_token": subject_token,
        "subject_token_type": "urn:ietf:params:oauth:token-type:access_token",
        "audience": "docvault-api",
    }, form=True)
    return status, json.loads(body)


def mcp(token, method, params=None):
    """One JSON-RPC call to the MCP server. Returns (status, WWW-Authenticate, message)."""
    message = {"jsonrpc": "2.0", "id": 1, "method": method}
    if params is not None:
        message["params"] = params
    headers = {"Accept": "application/json, text/event-stream", "MCP-Protocol-Version": "2025-06-18"}
    if token:
        headers["Authorization"] = f"Bearer {token}"
    status, response_headers, body = http("POST", MCP, message, headers)
    data = [line[5:].strip() for line in body.splitlines() if line.startswith("data:")]
    text = data[0] if data else body
    try:
        parsed = json.loads(text) if text else {}
    except json.JSONDecodeError:
        parsed = {}
    return status, response_headers.get("WWW-Authenticate") or "", parsed


def tool(token, name, arguments=None):
    """Call a tool. Returns (http status, challenge, isError, text)."""
    status, challenge, message = mcp(token, "tools/call", {"name": name, "arguments": arguments or {}})
    result = message.get("result", {})
    text = (result.get("content") or [{}])[0].get("text", "")
    return status, challenge, bool(result.get("isError")), text


def lines_of(path):
    try:
        with open(path, encoding="utf-8", errors="replace") as file:
            return file.read().splitlines()
    except OSError:
        return []


def keycloak_events(event_type, user_id):
    """Keycloak's stored events of one type for one user, newest first. None if not readable."""
    status, _, body = http("POST", f"{KEYCLOAK}/realms/master/protocol/openid-connect/token", {
        "grant_type": "password", "client_id": "admin-cli", "username": ADMIN_USER, "password": ADMIN_PASSWORD,
    }, form=True)
    if status != 200:
        return None
    query = urllib.parse.urlencode({"type": event_type, "user": user_id, "max": 50})
    status, _, body = http("GET", f"{KEYCLOAK}/admin/realms/{REALM}/events?{query}",
                           headers={"Authorization": "Bearer " + json.loads(body)["access_token"]})
    return json.loads(body) if status == 200 else None


def audit_checks(skip, tokens, alice_claims, carol_claims, document_id):
    """What the MCP server wrote about the calls above, and what the API logged."""
    print("\nThe audit log")
    if not AUDIT_LOG:
        print("  SKIP  the audit log (MCP_AUDIT_LOG is not set; `make mcp` and `make assert-mcp` set it)")
        return
    lines = lines_of(AUDIT_LOG)[skip:]
    records = []
    for line in lines:
        try:
            records.append(json.loads(line))
        except ValueError:
            records.append({})
    check("every line of the audit file is one JSON record", bool(records) and all(records), f"{len(lines)} lines")

    def find(**want):
        return next((r for r in records if all(r.get(k) == v for k, v in want.items())), None)

    listed = find(event="tool.call", tool="list_documents", outcome="ok", **{"user.id": alice_claims.get("sub")})
    check("alice's list call is on record: her id, the client, the token's id",
          bool(listed) and listed.get("client.id") == CLIENT and listed.get("user.name") == "alice"
          and listed.get("token.id") == alice_claims.get("jti"), json.dumps(listed)[:200])

    challenged = find(event="scope.challenge", tool="create_document", **{"user.id": alice_claims.get("sub")})
    check("the refused create is on record as a scope challenge naming docvault:write",
          bool(challenged) and challenged.get("scope.required") == "docvault:write" and challenged.get("outcome") == "refused",
          json.dumps(challenged)[:200])

    if document_id:
        refused = find(event="tool.call", tool="delete_document", outcome="refused", **{"document.id": document_id})
        check("alice's refused delete is on record as the API's refusal, not the scope's",
              bool(refused) and refused.get("reason") == "api_forbidden" and refused.get("user.name") == "alice",
              json.dumps(refused)[:200])
        deleted = find(event="tool.call", tool="delete_document", outcome="ok", **{"document.id": document_id})
        check("carol's delete of the same document is on record under her id",
              bool(deleted) and deleted.get("user.id") == carol_claims.get("sub"), json.dumps(deleted)[:200])

    text = "\n".join(lines)
    leaked = [t for t in tokens if t and (t in text or any(part in text for part in t.split(".")))]
    check("no record holds a token or the client secret", not leaked and SECRET not in text, f"{len(leaked)} tokens found")

    # Keycloak keeps its own record of the same session: the token the audit log
    # names by id, and the MCP server exchanging it.
    issued = keycloak_events("CODE_TO_TOKEN", alice_claims.get("sub"))
    exchanges = keycloak_events("TOKEN_EXCHANGE", alice_claims.get("sub"))
    if issued is None or exchanges is None:
        print("  SKIP  Keycloak's events (the admin API was not readable with KEYCLOAK_ADMIN_USER/PASSWORD)")
    else:
        token_event = next((e for e in issued if e.get("details", {}).get("token_id") == (listed or {}).get("token.id")), None)
        exchange_event = next((e for e in exchanges
                               if e.get("sessionId") == (listed or {}).get("session.id")
                               and e.get("clientId") == MCP
                               and e.get("details", {}).get("subject_token_client_id") == CLIENT
                               and e.get("details", {}).get("audience") == "docvault-api"), None)
        check("Keycloak's events hold the same token id and session, and the MCP server's exchange",
              bool(listed) and bool(token_event) and token_event.get("clientId") == CLIENT and bool(exchange_event),
              f"CODE_TO_TOKEN={'found' if token_event else 'missing'} TOKEN_EXCHANGE={'found' if exchange_event else 'missing'}")

    if not API_LOG:
        print("  SKIP  the API's access log (DOCVAULT_API_LOG is not set; `make api` writes .lab/api.log)")
        return
    trace = (listed or {}).get("trace.id")
    line = None
    for _ in range(10):  # the API writes its line after it has answered
        line = next((l for l in lines_of(API_LOG) if trace and f"trace={trace}" in l), None)
        if line:
            break
        time.sleep(0.5)
    check("the API logged the same request: same trace id, alice, and the MCP server as the client",
          bool(line) and "user=alice" in line and f"client={MCP}" in line and "status=200" in line,
          (line or f"no API log line with trace={trace}")[:200])


def main():
    if not SECRET:
        sys.exit("MCP_CLIENT_SECRET is not set (terraform output -raw mcp_server_client_secret).")
    audit_start = len(lines_of(AUDIT_LOG)) if AUDIT_LOG else 0
    parts = urllib.parse.urlparse(MCP)
    metadata_url = f"{parts.scheme}://{parts.netloc}/.well-known/oauth-protected-resource{parts.path}"

    print("Discovery")
    _, _, body = http("GET", f"{KEYCLOAK}/realms/{REALM}/.well-known/oauth-authorization-server")
    server = json.loads(body)
    check("Keycloak advertises PKCE S256 (MCP clients refuse an AS without it)",
          "S256" in server.get("code_challenge_methods_supported", []))
    check("Keycloak advertises the iss response parameter (RFC 9207)",
          server.get("authorization_response_iss_parameter_supported") is True)

    status, challenge, _ = mcp(None, "tools/list")
    check("MCP server: no token -> 401 pointing at its metadata",
          status == 401 and f'resource_metadata="{metadata_url}"' in challenge, challenge)
    _, _, body = http("GET", metadata_url)
    metadata = json.loads(body)
    check("metadata names this server and this realm (RFC 9728)",
          metadata.get("resource") == MCP and metadata.get("authorization_servers") == [server["issuer"]],
          f"resource={metadata.get('resource')}")

    print("\nTokens Keycloak issues for the MCP server")
    alice = sign_in("alice", "openid docvault:read")
    if "access_token" not in alice:
        check("alice signs in through the MCP client", False, describe(alice))
        # Everything below needs her token. Exit non-zero: a bare return here
        # would end the script "successfully" on the likeliest regression.
        print(f"\n{passed} passed, {failed} failed")
        sys.exit(1)
    alice_claims = claims(alice["access_token"])
    # The consent screen should say what the assistant gets and nothing it does
    # not: Keycloak's built-in `roles` scope lists "User roles" unless the
    # client drops it, although this client's token carries no roles.
    if consent_screens:
        screen = consent_screens[0]
        check("the consent screen names the assistant's permission and no roles",
              "Let the assistant list your documents" in screen and "User roles" not in screen, screen[:300])
    else:
        print("  SKIP  the consent screen (alice had already consented; shown on a fresh realm)")
    # Fails silently into aud=<client id> if the resource-indicators feature is off.
    check("aud is exactly the MCP server's URL (RFC 8707)", alice_claims.get("aud") == MCP, f"aud={alice_claims.get('aud')}")
    check("the token carries the consented scope", "docvault:read" in alice_claims.get("scope", "").split(), alice_claims.get("scope"))
    check("the MCP token carries no roles and no groups",
          not alice_claims.get("realm_access") and not alice_claims.get("resource_access") and not alice_claims.get("groups"))
    check("the authorization response carried iss", alice.get("iss_param") == server["issuer"], str(alice.get("iss_param")))

    refused = sign_in("alice", "openid docvault:read", resource="https://evil.example/mcp")
    check("a token for an unknown resource is refused by Keycloak", refused.get("error") == "invalid_target", describe(refused))

    print("\nAudience binding, both ways")
    status, _, _ = http("GET", f"{API}/documents", headers={"Authorization": f"Bearer {alice['access_token']}"})
    check("the API refuses the MCP token (so passthrough could not work)", status == 401, f"HTTP {status}")
    status, exchanged = exchange(alice["access_token"], MCP, SECRET)
    # Never print a token, not even part of one: CI logs are public.
    check("the MCP server can exchange it for an API token (RFC 8693)", status == 200 and "access_token" in exchanged,
          "" if "access_token" in exchanged else describe(exchanged))
    if "access_token" in exchanged:
        api_claims = claims(exchanged["access_token"])
        audience = api_claims.get("aud")
        check("the exchanged token is for the API only", audience in ("docvault-api", ["docvault-api"]), f"aud={audience}")
        # How the API can tell an assistant's request from the user's own.
        check("the exchanged token names the MCP server as its client (azp)", api_claims.get("azp") == MCP, f"azp={api_claims.get('azp')}")
        check("it is still alice, with her tenant group",
              api_claims.get("preferred_username") == "alice" and api_claims.get("groups") == ["/acme/engineering"],
              f"groups={api_claims.get('groups')}")
        status, _, _ = mcp(exchanged["access_token"], "tools/list")
        check("the MCP server refuses the API token", status == 401, f"HTTP {status}")

    carol = sign_in("carol", "openid docvault:read docvault:delete")
    status, carol_exchanged = exchange(carol.get("access_token", ""), MCP, SECRET)
    if "access_token" in carol_exchanged:
        carol_api = claims(carol_exchanged["access_token"])
        roles = carol_api.get("resource_access", {}).get("docvault-api", {}).get("roles", [])
        # carol is a platform-admin. An assistant acting for her must not be one.
        check("an administrator's exchanged token carries document roles but no realm roles",
              "doc.admin" in roles and not carol_api.get("realm_access"), f"realm_access={carol_api.get('realm_access')}")
    else:
        check("carol's token can be exchanged", False, describe(carol_exchanged))

    print("\nTools, as each demo user")
    status, _, message = mcp(alice["access_token"], "tools/list")
    names = sorted(t["name"] for t in message.get("result", {}).get("tools", []))
    check("every tool is listed", names == ["create_document", "delete_document", "list_documents"], str(names))

    status, _, is_error, text = tool(alice["access_token"], "list_documents")
    check("alice lists her tenant's documents", status == 200 and not is_error and '"tenant":"acme"' in text, text[:60])

    bob = sign_in("bob", "openid docvault:read")
    _, _, is_error, text = tool(bob.get("access_token"), "list_documents")
    check("bob gets his own tenant, not alice's", not is_error and '"tenant":"globex"' in text and "Acme" not in text, text[:60])

    dave = sign_in("dave", "openid docvault:read")
    status, _, is_error, text = tool(dave.get("access_token"), "list_documents")
    # The text matters: without the API audience mapper on the MCP server's client,
    # dave fails earlier, at the exchange, and that is an error too.
    check("dave (no roles) is refused by the API, not at the exchange",
          status == 200 and is_error and "your DocVault role" in text, text[:60])

    status, challenge, _, _ = tool(alice["access_token"], "create_document", {"title": "x"})
    check("a call without the scope gets 403 insufficient_scope naming it",
          status == 403 and 'error="insufficient_scope"' in challenge and 'scope="docvault:write"' in challenge, challenge[:90])

    writer = sign_in("alice", "openid docvault:read docvault:write docvault:delete")
    status, _, is_error, text = tool(writer.get("access_token"), "create_document", {"title": "assert-mcp-authz"})
    check("with the scope, alice (editor) creates a document", status == 200 and not is_error, text[:60])
    document_id = json.loads(text).get("id") if not is_error and text.startswith("{") else None
    if document_id:
        # The scope says the assistant may delete. alice's role says she may not.
        _, _, is_error, text = tool(writer["access_token"], "delete_document", {"id": document_id})
        check("the delete scope does not give alice the admin role", is_error and "your DocVault role" in text, text[:60])
        _, _, is_error, text = tool(carol.get("access_token"), "delete_document", {"id": document_id})
        check("carol (admin) deletes it", not is_error, text[:60])
        _, _, is_error, text = tool(bob.get("access_token"), "list_documents")
        check("the document never appeared in bob's tenant", "assert-mcp-authz" not in text)

    tokens = [t.get("access_token") for t in (alice, bob, dave, carol, writer)]
    carol_claims = claims(carol["access_token"]) if "access_token" in carol else {}
    audit_checks(audit_start, tokens, alice_claims, carol_claims, document_id)

    print(f"\n{passed} passed, {failed} failed")
    sys.exit(0 if failed == 0 else 1)


if __name__ == "__main__":
    main()
