#!/usr/bin/env python3
# DocVault - a Keycloak proof-of-concept lab.
# Copyright (C) 2026 Magnus Johansson
# SPDX-License-Identifier: GPL-3.0-or-later
# See the LICENSE file for the full licence text.

"""End-to-end check of the legacy SAML application against the REAL stack.

Needs: `make up`, `make seed`, and the application on :5003 (`make reports`).
`make assert-saml-app` runs it; CI runs it in the realm job.

Here Keycloak is the SAML identity provider and apps/saml-reports is the
service provider. The application's own tests (DocVault.Reports.Tests) forge
responses with a key they control, which this cannot do. What this adds is the
real Keycloak: that the two agree on signatures, names and addresses, and that
the application refuses what Keycloak really issues when it arrives changed,
twice, in the wrong browser, or for somebody else.

It reuses the browser and the response-changing helpers of assert-saml.py,
which sits next to it. Standard library only.
"""
import base64
import importlib.util
import json
import os
import re
import signal
import sys
import urllib.parse

_here = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location("assert_saml", os.path.join(_here, "assert-saml.py"))
saml = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(saml)

APP = os.environ.get("REPORTS_URL", "http://localhost:5003")
ENTITY_ID = f"{APP}/saml"
ACS = f"{APP}/saml/acs"
KEYCLOAK = saml.KEYCLOAK
REALM = saml.REALM
PASSWORD = saml.PASSWORD

passed = failed = 0


def check(label, ok, detail=""):
    global passed, failed
    passed, failed = passed + bool(ok), failed + (not ok)
    print(f"  {'PASS' if ok else 'FAIL'}  {label}" + (f"  [{detail}]" if detail else ""))


class Visit:
    """One person's browser going to the application."""

    def __init__(self):
        self.browser = saml.Browser()
        self.request = None    # the SAMLRequest the application sent, as XML
        self.response = None   # the SAMLResponse Keycloak sent back, base64, untouched
        self.status = None
        self.body = ""

    def get(self, path):
        return self.browser.request("GET", APP + path)

    def sign_in(self, username, password=PASSWORD, path="/me", through_legacy=False,
                change_request=None, change_response=None, stop_before_response=False):
        """Follow the application to Keycloak and back.

        through_legacy: at Keycloak, choose the legacy SAML provider instead of typing a password there.
        stop_before_response: return with Keycloak's response in hand, not yet delivered.
        """
        status, headers, body = self.get(path)
        typed = False
        for _ in range(20):
            if status in (302, 303):
                status, headers, body = self.browser.request("GET", urllib.parse.urljoin(APP, headers["Location"]))
                continue
            action, fields = saml.first_form(body)
            if not action:
                break
            target = urllib.parse.urljoin(KEYCLOAK, action)
            if "SAMLRequest" in fields and target.startswith(f"{KEYCLOAK}/realms/{REALM}/"):
                self.request = saml.xml_of(fields, "SAMLRequest")
                if change_request:
                    fields = change_request(fields)
            elif "SAMLResponse" in fields and target.startswith(APP):
                self.response = fields["SAMLResponse"]
                if stop_before_response:
                    return self
                if change_response:
                    fields = change_response(fields)
            elif "SAMLRequest" in fields or "SAMLResponse" in fields:
                pass  # between Keycloak and the legacy provider: passed on untouched
            elif through_legacy and f"broker/{saml.ALIAS}/login" in body and f"/realms/{REALM}/" in target:
                link = re.search(rf'href="([^"]*broker/{saml.ALIAS}/login[^"]*)"', body)
                status, headers, body = self.browser.request(
                    "GET", urllib.parse.urljoin(KEYCLOAK, saml.html.unescape(link.group(1))))
                continue
            elif 'name="password"' in body and not typed:
                fields = {"username": username, "password": password}
                typed = True
            else:
                break
            status, headers, body = self.browser.request("POST", target, fields)
        self.status, self.body = status, body
        return self

    def deliver(self, saml_response):
        """Post a response to the application from this browser."""
        self.status, _, self.body = self.browser.request("POST", ACS, {"SAMLResponse": saml_response})
        return self

    def me(self):
        status, _, body = self.get("/me")
        try:
            return json.loads(body) if status == 200 else None
        except json.JSONDecodeError:
            return None

    def report(self):
        status, _, body = self.get("/reports/summary")
        try:
            return status, json.loads(body) if status == 200 else {}
        except json.JSONDecodeError:
            return status, {}

    def refusal(self):
        """The application's word for why it refused, if it did."""
        try:
            return json.loads(self.body).get("detail") if self.status == 400 else None
        except json.JSONDecodeError:
            return None


def who(visit):
    me = visit.me()
    return f"{me.get('username')} in {me.get('tenant')}" if me else f"no session (HTTP {visit.status}, {visit.refusal()})"


def main():
    status, _, _ = saml.http("GET", APP + "/health")
    if status != 200:
        sys.exit(f"The legacy SAML application is not running on {APP} (make reports).")

    # ------------------------------------------------------------------ #
    print("\nThe application sends people to Keycloak")
    status, headers, _ = saml.http("GET", APP + "/me")
    check("without a session it redirects to its sign-in", status == 302 and "/saml/login" in headers.get("Location", ""))

    alice = Visit().sign_in("alice")
    me = alice.me() or {}
    check("alice signs in at Keycloak and the application knows her",
          me.get("username") == "alice" and me.get("tenant") == "acme" and me.get("groups") == ["/acme/engineering"], who(alice))
    status, report = alice.report()
    check("she gets Acme's report", status == 200 and report.get("tenant") == "acme", f"HTTP {status}")

    bob = Visit().sign_in("bob")
    status, report = bob.report()
    check("bob gets Globex's report, not Acme's", status == 200 and report.get("tenant") == "globex", f"HTTP {status} {who(bob)}")
    dave = Visit().sign_in("dave")
    status, _ = dave.report()
    check("dave, in no group, is signed in and refused the report with a 403",
          (dave.me() or {}).get("username") == "dave" and status == 403, f"HTTP {status}")
    check("a wrong password leaves no session", Visit().sign_in("alice", "not-her-password").me() is None)

    # ------------------------------------------------------------------ #
    print("\nEveryone the realm can sign in reaches the application")
    erin = Visit().sign_in("erin")
    check("erin, from the LDAP directory", (erin.me() or {}).get("tenant") == "acme", who(erin))
    heidi = Visit().sign_in("heidi", through_legacy=True)
    me = heidi.me() or {}
    check("heidi, from the legacy SAML provider: SAML in, and SAML out again",
          me.get("username") == f"{saml.ALIAS}.heidi" and me.get("tenant") == "acme", who(heidi))

    # ------------------------------------------------------------------ #
    print("\nWhat goes over the wire")
    response = base64.b64decode(alice.response).decode()
    _, assertion, _ = saml.parts(response)
    check("the application's request is signed", "<dsig:Signature" in alice.request or "<Signature" in alice.request)
    unsigned = Visit().sign_in("alice", change_request=lambda f: saml.with_xml(
        f, re.sub(r"<(\w+:)?Signature\b.*?</(\w+:)?Signature>", "", saml.xml_of(f, "SAMLRequest"), flags=re.S), "SAMLRequest"))
    check("Keycloak refuses a request that is not signed", unsigned.me() is None, who(unsigned))
    check("Keycloak signs the response and the assertion inside it",
          len(saml.SIGNATURE.findall(response)) == 2 and len(saml.SIGNATURE.findall(assertion)) == 1)
    check("the assertion is addressed to this application and nobody else",
          re.findall(r"<saml:Audience>([^<]+)", assertion) == [ENTITY_ID] and f'Destination="{ACS}"' in response)
    # Not an opaque identifier: Keycloak cannot keep a persistent NameID for a
    # person from the read-only directory (see the use case).
    name_id = re.search(r"<saml:NameID[^>]*>([^<]+)", assertion)
    check("the subject is the username", bool(name_id) and name_id.group(1) == "alice",
          name_id.group(1) if name_id else "no NameID")
    check("no roles are sent: only the username, the email address and the groups",
          sorted(set(re.findall(r'<saml:Attribute [^>]*\bName="([^"]+)"', assertion))) == ["email", "groups", "username"],
          str(sorted(set(re.findall(r'<saml:Attribute [^>]*\bName="([^"]+)"', assertion)))))

    # ------------------------------------------------------------------ #
    print("\nA response changed on its way to the application")
    for label, change in (
        ("with every signature removed", saml.no_signatures),
        ("with the assertion's signature removed", saml.no_assertion_signature),
        ("saying another person, signatures left in place",
         lambda f: saml.with_xml(f, saml.xml_of(f).replace(">alice<", ">carol<"))),
        ("saying another group, signatures left in place",
         lambda f: saml.with_xml(f, saml.xml_of(f).replace(">/acme/engineering<", ">/globex<"))),
    ):
        visit = Visit().sign_in("alice", change_response=change)
        check(f"is refused {label}", visit.status == 400 and visit.me() is None, who(visit))

    def forged(fields, around):
        before, signed, after = saml.parts(saml.xml_of(fields))
        copy = (saml.SIGNATURE.sub("", signed).replace(">alice<", ">carol<")
                .replace(">/acme/engineering<", ">/acme/legal<").replace('ID="ID_', 'ID="ID_forged', 1))
        if around:
            copy = copy.replace("</saml:Assertion>", "<saml:Advice>" + signed + "</saml:Advice></saml:Assertion>")
            return saml.with_xml(fields, saml.SIGNATURE.sub("", before) + copy + after)
        return saml.with_xml(fields, saml.SIGNATURE.sub("", before) + copy + signed + after)

    for label, around in (("in front of", False), ("wrapped around", True)):
        visit = Visit().sign_in("alice", change_response=lambda f, a=around: forged(f, a))
        check(f"is refused with a forged assertion {label} the signed one",
              visit.status == 400 and visit.me() is None, who(visit))

    # ------------------------------------------------------------------ #
    print("\nA real response in the wrong place")
    waiting = Visit().sign_in("alice", stop_before_response=True)
    thief = Visit().sign_in("bob", stop_before_response=True)   # mid-way through a sign-in of their own
    thief.deliver(waiting.response)
    check("alice's response, posted from another browser's sign-in, is refused",
          thief.refusal() == "in_response_to" and thief.me() is None, who(thief))

    # Found in review. The envelope of a response is outside the assertion's
    # signature, and the application accepts a response whose outer signature is
    # gone. So the thief removes it and rewrites the envelope to answer their
    # own sign-in. Only the request id inside the signed assertion still says
    # whose sign-in this was.
    thief = Visit().sign_in("bob", stop_before_response=True)
    thief_request = re.search(r'\bID="([^"]+)"', thief.request).group(1)
    stolen = saml.no_envelope_signature({"SAMLResponse": waiting.response})
    readdressed = re.sub(r'InResponseTo="[^"]+"', f'InResponseTo="{thief_request}"', saml.xml_of(stolen), count=1)
    thief.deliver(base64.b64encode(readdressed.encode()).decode())
    check("readdressed on its unsigned envelope to the thief's own sign-in, it is still refused",
          readdressed != saml.xml_of(stolen) and thief.refusal() == "in_response_to" and thief.me() is None, who(thief))

    stranger = Visit().deliver(waiting.response)
    check("posted from a browser that never started a sign-in, it is refused",
          stranger.refusal() == "unsolicited" and stranger.me() is None, who(stranger))

    waiting.deliver(waiting.response)
    check("in alice's own browser it signs her in", (waiting.me() or {}).get("username") == "alice", who(waiting))
    # A copy of a browser's cookies taken before it delivers its response, and
    # the same response: everything a thief on that machine could have.
    replay = Visit().sign_in("alice", stop_before_response=True)
    held = dict(replay.browser.cookies)
    replay.deliver(replay.response)
    twin = Visit()
    twin.browser.cookies = held
    twin.deliver(replay.response)
    check("the same response, with a copy of the same browser's cookies, is not accepted twice",
          twin.refusal() == "replay" and twin.me() is None, who(twin))

    # A response Keycloak signed for a different application.
    other_client = "https://another-application.example/saml"
    try:
        saml.admin("POST", "/clients", {
            "clientId": other_client, "protocol": "saml", "enabled": True, "redirectUris": ["http://localhost:43999/acs"],
            "attributes": {
                "saml_idp_initiated_sso_url_name": "another-application",
                "saml_assertion_consumer_url_post": "http://localhost:43999/acs",
                "saml.assertion.signature": "true", "saml.server.signature": "true", "saml.client.signature": "false",
            },
        })
        browser = saml.Browser()
        status, headers, body = browser.request(
            "GET", f"{KEYCLOAK}/realms/{REALM}/protocol/saml/clients/another-application")
        for_other = None
        for _ in range(8):
            if status in (302, 303):
                status, headers, body = browser.request("GET", headers["Location"])
                continue
            action, fields = saml.first_form(body)
            if not action or "SAMLResponse" in fields:
                for_other = fields.get("SAMLResponse")
                break
            status, headers, body = browser.request(
                "POST", urllib.parse.urljoin(KEYCLOAK, action), {"username": "alice", "password": PASSWORD})
        audience = re.findall(r"<saml:Audience>([^<]+)", base64.b64decode(for_other or "").decode())
        visit = Visit().sign_in("alice", change_response=lambda f: dict(f, SAMLResponse=for_other))
        check("a response Keycloak signed for another application is refused",
              audience == [other_client] and visit.status == 400 and visit.me() is None, f"audience {audience}; {who(visit)}")
    finally:
        for found in saml.admin("GET", "/clients?clientId=" + urllib.parse.quote(other_client))[1] or []:
            saml.admin("DELETE", f"/clients/{found['id']}")

    # ------------------------------------------------------------------ #
    print("\nAfter sign-in")
    visit = Visit().sign_in("alice", path="/saml/login?returnUrl=" + urllib.parse.quote("https://evil.example/"))
    check("a sign-in that asked to end at another site ends inside the application",
          (visit.me() or {}).get("username") == "alice" and "evil.example" not in visit.body, who(visit))

    session = Visit().sign_in("alice", path="/")
    status, _, _ = session.browser.request("POST", APP + "/saml/logout", {})
    check("a sign-out that is not from the application's own page is refused",
          status == 400 and session.me() is not None, f"HTTP {status}")
    token = re.search(r'type="hidden" name="([^"]+)" value="([^"]+)"', session.body)
    status, _, _ = session.browser.request(
        "POST", APP + "/saml/logout", {token.group(1): saml.html.unescape(token.group(2))}) if token else (0, None, None)
    status_after, headers_after, _ = session.get("/me")
    check("from its own page, sign-out ends the application's session",
          status in (302, 303) and status_after == 302 and "/saml/login" in headers_after.get("Location", ""),
          f"HTTP {status}, then {status_after}")
    # Stated, not hidden: there is no single logout. Keycloak still knows her.
    session.sign_in("alice", password="not-needed-and-not-typed")
    check("she is still signed in at Keycloak: the next visit needs no password (stated limit)",
          (session.me() or {}).get("username") == "alice", who(session))


def interrupted(signum, _frame):
    raise SystemExit(f"interrupted by signal {signum}")


if __name__ == "__main__":
    signal.signal(signal.SIGTERM, interrupted)
    try:
        main()
    finally:
        print(f"\n{passed} passed, {failed} failed")
    sys.exit(1 if failed else 0)
