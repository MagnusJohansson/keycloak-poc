#!/usr/bin/env python3
# DocVault - a Keycloak proof-of-concept lab.
# Copyright (C) 2026 Magnus Johansson
# SPDX-License-Identifier: GPL-3.0-or-later
# See the LICENSE file for the full licence text.

"""End-to-end check of per-document authorization against the REAL stack:
Keycloak, the LDAP directory, OpenFGA and the API.

Needs: `make up`, `make seed`, and the API on :5001 (`make api`, which runs it
with Sharing:Mode OpenFga). `make assert-fga` runs it; CI runs it in the realm job.

The API's own tests answer OpenFGA's checks from a table, and the model's tests
run without the API. This is where the two meet: real tokens, real tuples, the
real model. It also does what neither can: freezes OpenFGA, and renames and moves
a person in the directory.

It creates documents and removes them, and changes the directory and puts it
back, also when a check fails. If a run is killed outright,
`docker compose -f infra/local/docker-compose.yml up -d --force-recreate ldap`
resets the directory.

Standard library only. The sign-in and directory helpers are tools/assert-ldap.py's.
"""
import importlib.util
import json
import os
import sys
import time
import urllib.parse
import uuid

_spec = importlib.util.spec_from_file_location(
    "assert_ldap", os.path.join(os.path.dirname(os.path.abspath(__file__)), "assert-ldap.py"))
ldap = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(ldap)

API = ldap.API
OPENFGA = os.environ.get("OPENFGA_URL", "http://localhost:8090")
OPENFGA_KEY = os.environ.get("OPENFGA_API_KEY", "docvault-fga-lab-key")
STORE = os.environ.get("FGA_STORE_ID", "")
MODEL = os.environ.get("FGA_MODEL_ID", "")
# Acme Q3 Roadmap: seeded, visible to acme, with no owner OpenFGA knows of.
ROADMAP = "d0c00000-0000-4000-8000-000000000001"
# Acme Acquisition Memo: seeded, classified.
MEMO = "d0c00000-0000-4000-8000-000000000003"

passed = failed = 0


def check(label, ok, detail=""):
    global passed, failed
    passed, failed = passed + bool(ok), failed + (not ok)
    print(f"  {'PASS' if ok else 'FAIL'}  {label}" + (f"  [{detail}]" if detail else ""))


class Person:
    """Someone signed in through the real login page."""

    def __init__(self, username, password=ldap.PASSWORD):
        ldap.forgive()
        response = ldap.sign_in(username, password)
        if "access_token" not in response:
            sys.exit(f"{username} could not sign in: {ldap.describe(response)}")
        self.username = username
        self.token = response["access_token"]
        self.refresh_token = response.get("refresh_token")
        self.sub = ldap.claims(self.token)["sub"]

    def refresh(self):
        _, response = ldap.refresh(self.refresh_token)
        if "access_token" in response:
            self.token, self.refresh_token = response["access_token"], response.get("refresh_token")
        return response

    def call(self, method, path, data=None):
        status, _, body = ldap.http(method, API + path, data, headers={"Authorization": f"Bearer {self.token}"})
        try:
            return status, json.loads(body) if body else {}
        except json.JSONDecodeError:
            return status, {}

    def titles(self):
        status, body = self.call("GET", "/documents")
        return [d["title"] for d in body.get("documents", [])] if status == 200 else f"HTTP {status}"

    def sees(self, title):
        """True or False from a list that came back; None when no list did.

        Never `title not in titles()`: a refused list (403, 503) would pass it.
        """
        titles = self.titles()
        return title in titles if isinstance(titles, list) else None


def fga(method, path, data=None, key=OPENFGA_KEY):
    headers = {"Authorization": f"Bearer {key}"} if key else {}
    status, _, body = ldap.http(method, f"{OPENFGA}/stores/{STORE}/{path}", data, headers=headers)
    return status, json.loads(body) if body else {}


def stored(document_id):
    """The tuples OpenFGA holds for a document, as (user, relation)."""
    _, body = fga("POST", "read", {"tuple_key": {"object": f"document:{document_id}"}})
    return sorted((t["key"]["user"], t["key"]["relation"]) for t in body.get("tuples", []))


def main():
    if not STORE or not MODEL:
        sys.exit("FGA_STORE_ID and FGA_MODEL_ID are needed (make assert-fga reads them from 25-openfga).")
    ldap.compose("unpause", "openfga")  # left frozen if an earlier run was killed mid-outage
    status, _, _ = ldap.http("GET", API + "/documents")
    if status != 401:
        sys.exit(f"The API is not answering on {API} (make api): HTTP {status}")

    alice, carol, bob, erin = Person("alice"), Person("carol"), Person("bob"), Person("erin")
    created = []

    def create(person, title, visibility=None):
        body = {"title": title} | ({"visibility": visibility} if visibility else {})
        status, document = person.call("POST", "/documents", body)
        if status == 201:
            created.append((person, document["id"]))
        return status, document

    try:
        # ------------------------------------------------------------------ #
        print("\nOpenFGA keeps its own door shut")
        status, _ = fga("POST", "read", {}, key=None)
        check("a request without the key is refused", status == 401, f"HTTP {status}")
        status, _ = fga("POST", "read", {}, key="not-the-key")
        check("a request with another key is refused", status == 401, f"HTTP {status}")

        # ------------------------------------------------------------------ #
        print("\nWho sees what")
        status, private = create(alice, "assert-fga private", "private")
        check("alice creates a private document", status == 201 and private.get("visibility") == "private", f"HTTP {status}")
        pid = private.get("id")
        check("OpenFGA records its tenant and its owner by user id, and no tenant-wide viewers",
              stored(pid) == sorted([("tenant:acme", "tenant"), (f"user:{alice.sub}", "owner")]), str(stored(pid)))
        status, tenant_doc = create(alice, "assert-fga for everyone")
        check("a document is visible to its tenant unless made private",
              status == 201 and ("tenant:acme#member", "viewer") in stored(tenant_doc.get("id")), str(stored(tenant_doc.get("id"))))

        check("alice sees her private document", alice.sees("assert-fga private") is True)
        check("carol, in the same tenant and an administrator, does not",
              carol.sees("assert-fga private") is False and carol.sees("assert-fga for everyone") is True)
        hidden, _ = carol.call("GET", f"/documents/{pid}")
        missing, _ = carol.call("GET", "/documents/00000000-0000-4000-8000-000000000000")
        check("for carol it is a 404, as for a document that does not exist", hidden == missing == 404, f"{hidden} {missing}")
        check("bob in globex sees neither",
              bob.sees("assert-fga private") is False and bob.sees("assert-fga for everyone") is False)
        status, _ = carol.call("GET", f"/documents/{MEMO}")
        check("a classified document is not reachable by id, only through the step-up endpoint", status == 404, f"HTTP {status}")

        # ------------------------------------------------------------------ #
        print("\nSharing")
        status, _ = carol.call("POST", f"/documents/{pid}/shares", {"username": "carol"})
        check("someone who cannot see a document cannot share it (404)", status == 404, f"HTTP {status}")
        status, _ = alice.call("POST", f"/documents/{ROADMAP}/shares", {"username": "carol"})
        check("a seeded document has no owner, so not even alice can share it (403)", status == 403, f"HTTP {status}")

        status, elsewhere = alice.call("POST", f"/documents/{pid}/shares", {"username": "bob"})
        _, nobody = alice.call("POST", f"/documents/{pid}/shares", {"username": "no-such-person"})
        same = {k: v for k, v in elsewhere.items() if k != "traceId"} == {k: v for k, v in nobody.items() if k != "traceId"}
        check("bob, in another tenant, cannot be shared with, and looks like nobody", status == 404 and same, f"HTTP {status}")

        status, share = alice.call("POST", f"/documents/{pid}/shares", {"username": "carol"})
        check("alice shares it with carol", status == 201, f"HTTP {status}")
        check("OpenFGA records carol by user id, not by name",
              (f"user:{carol.sub}", "viewer") in stored(pid) and not any("carol" in u for u, _ in stored(pid)))
        check("carol sees it now", carol.sees("assert-fga private") is True)
        status, _ = carol.call("POST", f"/documents/{pid}/shares", {"username": "erin"})
        check("carol can see it but not share it on (403)", status == 403, f"HTTP {status}")

        status, _ = alice.call("DELETE", f"/documents/{pid}/shares/users/{carol.sub}")
        check("alice revokes carol's share", status == 204, f"HTTP {status}")
        check("and the very next request no longer shows it to carol", carol.sees("assert-fga private") is False)

        status, _ = alice.call("POST", f"/documents/{pid}/shares", {"department": "engineering"})
        check("alice shares it with her department, engineering", status == 201, f"HTTP {status}")
        check("erin, from the directory, in engineering, sees it", erin.sees("assert-fga private") is True)
        check("carol, in legal, does not", carol.sees("assert-fga private") is False)
        status, _ = alice.call("POST", f"/documents/{pid}/shares", {"department": "sales"})
        check("a department the tenant does not have cannot be shared with", status == 404, f"HTTP {status}")

        # OpenFGA's own answer, past the API: a share to bob written straight into
        # the store. The API would refuse bob before asking; the model must too.
        bob_share = {"user": f"user:{bob.sub}", "relation": "viewer", "object": f"document:{pid}"}
        fga("POST", "write", {"writes": {"tuple_keys": [bob_share]}})
        try:
            def as_bob(tenant):
                return fga("POST", "check", {
                    "authorization_model_id": MODEL, "consistency": "HIGHER_CONSISTENCY",
                    "tuple_key": {"user": f"user:{bob.sub}", "relation": "can_view", "object": f"document:{pid}"},
                    "contextual_tuples": {"tuple_keys": [{"user": f"user:{bob.sub}", "relation": "member", "object": f"tenant:{tenant}"}]},
                })[1].get("allowed")
            check("a share to bob written past the API: OpenFGA itself says no while he is in globex",
                  as_bob("globex") is False, str(as_bob("globex")))
            check("and yes if told he is in acme, which is why membership comes only from his token",
                  as_bob("acme") is True)
            check("the API still shows bob nothing", bob.sees("assert-fga private") is False)
        finally:
            fga("POST", "write", {"deletes": {"tuple_keys": [bob_share]}})

        # ------------------------------------------------------------------ #
        print("\nWhat a failed delete leaves")
        # A large delete that fails at its last write leaves the tenant, the owner and
        # the shares through which the person deleting it sees it (DeletingAsync). The
        # API tests check which tuples remain; whether those still let the deleter see
        # the document is the model's answer, asked here of the real OpenFGA. A private
        # document whose owner has left, shared with carol and with legal.
        left = f"document:{uuid.uuid4()}"
        remains = [
            {"user": "tenant:acme", "relation": "tenant", "object": left},
            {"user": f"user:{uuid.uuid4()}", "relation": "owner", "object": left},
            {"user": f"user:{carol.sub}", "relation": "viewer", "object": left},
            {"user": "department:acme/legal#member", "relation": "viewer", "object": left},
        ]
        fga("POST", "write", {"writes": {"tuple_keys": remains}})
        try:
            def can_view(sub, *departments):
                member = [{"user": f"user:{sub}", "relation": "member", "object": "tenant:acme"}]
                member += [{"user": f"user:{sub}", "relation": "member", "object": f"department:acme/{d}"} for d in departments]
                return fga("POST", "check", {
                    "authorization_model_id": MODEL, "consistency": "HIGHER_CONSISTENCY",
                    "tuple_key": {"user": f"user:{sub}", "relation": "can_view", "object": left},
                    "contextual_tuples": {"tuple_keys": member},
                })[1].get("allowed")
            check("carol, who was deleting it, still sees it through her own share",
                  can_view(carol.sub) is True, str(can_view(carol.sub)))
            check("someone in legal still sees it through the department's share",
                  can_view(uuid.uuid4(), "legal") is True)
            check("someone only in engineering does not",
                  can_view(uuid.uuid4(), "engineering") is False)
        finally:
            fga("POST", "write", {"deletes": {"tuple_keys": remains}})

        # ------------------------------------------------------------------ #
        print("\nThe directory changes under it")
        # A mover: out of engineering, still in Acme. Membership is not stored in
        # OpenFGA, so the department's share ends with erin's next token. With
        # Keycloak's cache off for directory people (ldap.tf), a refresh is one.
        acme_group = f"dn: cn=acme,ou=groups,{ldap.BASE}\nchangetype: modify\nreplace: member\n"
        try:
            ldap.directory(f"ldapmodify {ldap.AS_ADMIN}",
                           acme_group + f"member: cn=engineering,ou=groups,{ldap.BASE}\nmember: uid=erin,{ldap.PEOPLE}\n")
            ldap.set_engineering("grace")
            erin.refresh()
            groups = ldap.claims(erin.token).get("groups")
            titles = erin.titles()
            check("erin, moved out of engineering but still in Acme, loses the department's share at her next token",
                  groups == ["/acme"] and isinstance(titles, list) and "assert-fga for everyone" in titles
                  and "assert-fga private" not in titles, f"{groups} {titles}")
        finally:
            ldap.restore("put erin back in engineering", f"ldapmodify {ldap.AS_ADMIN}", (
                f"dn: cn=engineering,ou=groups,{ldap.BASE}\nchangetype: modify\nreplace: member\n"
                f"member: uid=erin,{ldap.PEOPLE}\n"))
            ldap.restore("take erin out of acme directly", f"ldapmodify {ldap.AS_ADMIN}",
                         acme_group + f"member: cn=engineering,ou=groups,{ldap.BASE}\n")
        erin.refresh()
        check("back in engineering, the share is hers again", erin.sees("assert-fga private") is True)

        # A rename. The share is to erin's user id, so it follows her to her new name.
        # This directory does not rewrite group members when a person is renamed, so
        # engineering is pointed at the new name too, as a real one would.
        status, _ = alice.call("POST", f"/documents/{pid}/shares", {"username": "erin"})
        check("alice shares it with erin by name", status == 201, f"HTTP {status}")
        renamed = None
        try:
            ldap.directory(f"ldapmodrdn {ldap.AS_ADMIN} -r uid=erin,{ldap.PEOPLE} uid=erin-renamed")
            ldap.set_engineering("erin-renamed")
            renamed = Person("erin-renamed")
            check("renamed in the directory, she signs in under the new name with the same user id",
                  renamed.sub == erin.sub and ldap.claims(renamed.token).get("preferred_username") == "erin-renamed")
            check("the share followed her", renamed.sees("assert-fga private") is True)
            _, shares = alice.call("GET", f"/documents/{pid}/shares")
            people = [(s.get("username"), s.get("inTenant")) for s in shares.get("shares", []) if s.get("type") == "user"]
            check("alice's list of shares shows her new name", people == [("erin-renamed", True)], str(people))
        finally:
            ldap.restore("rename erin back", f"ldapmodrdn {ldap.AS_ADMIN} -r uid=erin-renamed,{ldap.PEOPLE} uid=erin")
            ldap.restore("put erin back in engineering", f"ldapmodify {ldap.AS_ADMIN}", (
                f"dn: cn=engineering,ou=groups,{ldap.BASE}\nchangetype: modify\nreplace: member\n"
                f"member: uid=erin,{ldap.PEOPLE}\n"))
            # Keycloak takes the old name back at her next sign-in.
            if renamed is not None:
                restored = Person("erin")
                if restored.username != ldap.claims(restored.token).get("preferred_username"):
                    print("  WARNING  erin did not get her name back in Keycloak")

        # A leaver: out of every group, so out of Acme. The share to her id stays in
        # OpenFGA and grants nothing while she is elsewhere; the list must say so,
        # or it reads as access she still has. Keycloak reads her groups from the
        # directory on each lookup (no cache for directory people).
        try:
            ldap.set_engineering("grace")
            _, shares = alice.call("GET", f"/documents/{pid}/shares")
            people = [(s.get("username"), s.get("inTenant")) for s in shares.get("shares", []) if s.get("type") == "user"]
            check("erin, gone from Acme, is still in alice's list of shares, marked as no longer in it",
                  people == [("erin", False)], str(people))
        finally:
            ldap.restore("put erin back in engineering", f"ldapmodify {ldap.AS_ADMIN}", (
                f"dn: cn=engineering,ou=groups,{ldap.BASE}\nchangetype: modify\nreplace: member\n"
                f"member: uid=erin,{ldap.PEOPLE}\n"))

        # ------------------------------------------------------------------ #
        print("\nDeleting")
        status, _ = carol.call("DELETE", f"/documents/{pid}")
        check("carol, an administrator, cannot delete a private document she cannot see (404)", status == 404, f"HTTP {status}")
        status, _ = erin.call("DELETE", f"/documents/{tenant_doc.get('id')}")
        check("erin can see alice's tenant-wide document, and still cannot delete it (403)", status == 403, f"HTTP {status}")

        # ------------------------------------------------------------------ #
        print("\nWithout OpenFGA")
        before = alice.titles()
        code, out = ldap.compose("pause", "openfga")
        if code != 0:
            check("OpenFGA could be frozen", False, out.strip()[:120])
        else:
            try:
                started = time.monotonic()
                listed, body = alice.call("GET", "/documents")
                took = time.monotonic() - started
                check("the list is a 503, not an empty list", listed == 503 and "documents" not in body, f"HTTP {listed}")
                check("and it says so within a few seconds", took < 10, f"{took:.1f}s")
                status, _ = alice.call("GET", f"/documents/{pid}")
                check("one document is a 503, not a 404", status == 503, f"HTTP {status}")
                status, _ = create(alice, "assert-fga during the outage")
                check("a new document is refused with a 503", status == 503, f"HTTP {status}")
            finally:
                ldap.compose("unpause", "openfga")
            for _ in range(20):
                if alice.call("GET", "/documents")[0] == 200:
                    break
                time.sleep(1)
            check("afterwards the list is as it was, with nothing half-made", alice.titles() == before, str(alice.titles()))

        status, _ = alice.call("DELETE", f"/documents/{pid}")
        check("alice, an editor, deletes her own document", status == 204, f"HTTP {status}")
        check("and its relationships are gone from OpenFGA", stored(pid) == [], str(stored(pid)))
        created[:] = [(p, i) for p, i in created if i != pid]
    finally:
        ldap.compose("unpause", "openfga")
        for person, document_id in created:
            person.call("DELETE", f"/documents/{document_id}")

    print(f"\n{passed} passed, {failed} failed")
    sys.exit(0 if failed == 0 else 1)


if __name__ == "__main__":
    main()
