"""End-to-end smoke test against a real Postgres (estatehub_test), over gRPC-Web like the browser.

Covers what the in-memory integration tests can't: row-level security, the SECURITY DEFINER lookups,
Npgsql query translation, cookies over HTTP/1.1. Codes and links are read from Mailpit; this run's
messages are deleted at the end. Run it with tests/e2e/run-smoke.sh (starts a temporary API on :5368),
never against the dev database. Standard library only (python3 -I).
"""
import json
import time
import random
import re
import struct
import sys
import urllib.error
import urllib.parse
import urllib.request

API = "http://127.0.0.1:5368"
MAILPIT = "http://127.0.0.1:8025"
RUN = f"{random.randrange(16**6):06x}"
results = []


# ---------- tiny protobuf ----------
def varint(n):
    out = b""
    while True:
        b = n & 0x7F
        n >>= 7
        out += bytes([b | (0x80 if n else 0)])
        if not n:
            return out


def msg(*fields):
    out = b""
    for tag, value in fields:
        if value is None:
            continue
        if isinstance(value, bool):
            out += varint(tag << 3) + varint(int(value))
        else:
            data = value.encode() if isinstance(value, str) else value
            out += varint(tag << 3 | 2) + varint(len(data)) + data
    return out


def parse(data):
    fields, i = {}, 0
    while i < len(data):
        key, i = read_varint(data, i)
        tag, wire = key >> 3, key & 7
        if wire == 0:
            value, i = read_varint(data, i)
        elif wire == 2:
            length, i = read_varint(data, i)
            value, i = data[i:i + length], i + length
        else:
            raise ValueError(f"wire type {wire}")
        fields.setdefault(tag, []).append(value)
    return fields


def read_varint(data, i):
    shift = result = 0
    while True:
        b = data[i]
        i += 1
        result |= (b & 0x7F) << shift
        shift += 7
        if not b & 0x80:
            return result, i


def s(fields, tag):
    return fields.get(tag, [b""])[0].decode()


# ---------- gRPC-Web ----------
DEVICE = {"cookie": None}  # the "tablet's" HttpOnly device cookie, kept like a browser would


def call(method, body=b"", token=None, cookie=None):
    req = urllib.request.Request(f"{API}/{method}", data=b"\x00" + struct.pack(">I", len(body)) + body, method="POST")
    req.add_header("content-type", "application/grpc-web+proto")
    req.add_header("x-grpc-web", "1")
    req.add_header("user-agent", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) Chrome/152.0.0.0 Safari/537.36")
    if token:
        req.add_header("authorization", f"Bearer {token}")
    cookies = []
    if cookie:
        cookies.append(f"estatehub_refresh={cookie}")
    if DEVICE["cookie"] and "GuardDeviceService" in method:
        cookies.append(f"estatehub_device={DEVICE['cookie']}")
    if cookies:
        req.add_header("cookie", "; ".join(cookies))
    try:
        with urllib.request.urlopen(req) as resp:
            headers, raw = resp.headers, resp.read()
    except urllib.error.HTTPError as error:
        # Refused before reaching gRPC (e.g. authorization): plain HTTP status. gRPC clients, the
        # browser's included, map it like this.
        return {401: 16, 403: 7, 404: 12, 429: 14, 503: 14}.get(error.code, 2), f"HTTP {error.code}", {}, None
    status, message, payload, i = headers.get("grpc-status"), headers.get("grpc-message"), b"", 0
    while i < len(raw):
        flag, length = raw[i], struct.unpack(">I", raw[i + 1:i + 5])[0]
        frame = raw[i + 5:i + 5 + length]
        i += 5 + length
        if flag & 0x80:
            for line in frame.decode().split("\r\n"):
                if line.lower().startswith("grpc-status:"):
                    status = line.split(":", 1)[1].strip()
                if line.lower().startswith("grpc-message:"):
                    message = urllib.parse.unquote(line.split(":", 1)[1].strip())
        else:
            payload = frame
    set_cookie = " ".join(headers.get_all("set-cookie") or [])
    new_cookie = re.search(r"estatehub_refresh=([^;]*)", set_cookie)
    device = re.search(r"estatehub_device=([^;]*)", set_cookie)
    if device:
        DEVICE["cookie"] = device.group(1) or None
    return int(status or 0), message, parse(payload), (new_cookie.group(1) if new_cookie else None)


def check(name, condition, detail=""):
    results.append((name, bool(condition)))
    print(("PASS " if condition else "FAIL ") + name + (f"  ({detail})" if detail and not condition else ""))


# ---------- Mailpit ----------
def mail_text(address):
    query = urllib.parse.quote(f'to:"{address}"')
    with urllib.request.urlopen(f"{MAILPIT}/api/v1/search?query={query}") as r:
        messages = json.load(r)["messages"]
    if not messages:
        return ""
    with urllib.request.urlopen(f"{MAILPIT}/api/v1/message/{messages[0]['ID']}") as r:  # newest first
        m = json.load(r)
    return m["Subject"] + "\n" + m["Text"]


def code_for(address):
    found = re.search(r"\b(\d{6})\b", mail_text(address))
    return found.group(1) if found else ""


def delete_run_mail():
    query = urllib.parse.quote(RUN)
    urllib.request.urlopen(urllib.request.Request(f"{MAILPIT}/api/v1/search?query={query}", method="DELETE")).read()


# ---------- the run ----------
A = "estatehub.auth.v1.AuthService/"
M = "estatehub.members.v1.MembersService/"
P = "estatehub.platform.v1.PlatformService/"
F = "estatehub.features.v1.FeaturesService/"

st, _, admin, _ = call(A + "DevSignIn", msg((1, f"sanity-admin-{RUN}"), (3, "super_admin"), (4, True)))
check("dev super admin signs in", st == 0)
admin_token = s(admin, 1)

st, _, jwks, _ = call(A + "GetJwks")
check("JWKS served from the signing_keys table", st == 0 and '"kid"' in s(jwks, 1) and '"d"' not in s(jwks, 1))

estates = []
for n in (1, 2):
    st, err, e, _ = call(P + "CreateEstate", msg((1, f"Sanity {RUN} {n}"), (2, "sectional_title")), admin_token)
    check(f"create estate {n}", st == 0, err)
    estates.append(s(e, 1))
st, err, _, _ = call(P + "CreateEstate", msg((1, f"sanity {RUN} 1"), (2, "sectional_title")), admin_token)
check("duplicate estate name refused on Postgres (case-insensitive)", st == 3, f"{st} {err}")

st, _, mgr, _ = call(A + "DevSignIn", msg((1, f"sanity-mgr-{RUN}"), (2, estates[0]), (3, "manager"), (4, True)))
mgr_token = s(mgr, 1)
st2, _, mgr2, _ = call(A + "DevSignIn", msg((1, f"sanity-mgr2-{RUN}"), (2, estates[1]), (3, "manager"), (4, True)))
check("managers of both estates sign in (membership lookup function)", st == 0 and st2 == 0)

owner = f"owner-{RUN}@example.test"
st, err, _, _ = call(M + "Invite", msg((1, "Sanity Owner"), (2, owner), (4, "owner")), mgr_token)
check("manager invites an owner (RLS insert)", st == 0, err)
st, err, _, _ = call(M + "Invite", msg((1, "Too High"), (2, f"x-{RUN}@example.test"), (4, "manager")), mgr_token)
check("manager can't invite a manager", st == 7, f"{st} {err}")

st, err, listing, _ = call(M + "ListMembers", b"", mgr_token)
check("members list (join) works on Postgres", st == 0 and len(listing.get(1, [])) == 1 and len(listing.get(2, [])) == 1, f"{st} {err}")
st, _, other, _ = call(M + "ListMembers", b"", s(mgr2, 1))
check("estate 2 sees none of estate 1's invitations or members", st == 0 and len(other.get(2, [])) == 0 and len(other.get(1, [])) == 1)

link = re.search(r"/invite#([A-Za-z0-9_-]+)", mail_text(owner))
check("invitation email in Mailpit", link is not None)
token = link.group(1) if link else ""
st, err, info, _ = call(A + "GetInvitation", msg((1, token)))
check("invitation lookup through the SECURITY DEFINER function", st == 0 and s(info, 2) == "owner", f"{st} {err}")
st, _, otp, _ = call(A + "StartInvitation", msg((1, token)))
st, err, _, _ = call(A + "AcceptInvitation", msg((1, s(otp, 1)), (2, "000000")))
check("wrong invitation code refused", st == 16, f"{st} {err}")
st, err, accepted, cookie = call(A + "AcceptInvitation", msg((1, s(otp, 1)), (2, code_for(owner))))
signed = parse(accepted.get(1, [b""])[0])
check("invitation accepted, lands in estate 1 as owner", st == 0 and s(signed, 4) == estates[0] and s(signed, 5) == "owner", f"{st} {err}")
st, _, _, _ = call(A + "GetInvitation", msg((1, token)))
check("invitation link works once", st == 5)

st, err, refreshed, cookie2 = call(A + "Refresh", cookie=cookie)
check("refresh cookie rotates", st == 0 and cookie2 and cookie2 != cookie, f"{st} {err}")
st, err, sessions, _ = call(A + "ListSessions", b"", s(refreshed, 1))
devices = [s(parse(item), 2) for item in sessions.get(1, [])]
check("sessions list on Postgres", st == 0 and devices == ["Chrome on macOS"], f"{st} {err} {devices}")

st, _, otp, _ = call(A + "RequestOtp", msg((1, f"nobody-{RUN}@example.test")))
check("unknown address gets a normal answer", st == 0 and s(otp, 2) == "email")
st, _, otp, _ = call(A + "RequestOtp", msg((1, owner)))
st, err, step, _ = call(A + "VerifyOtp", msg((1, s(otp, 1)), (2, code_for(owner))))
check("owner signs in with an emailed code", st == 0 and 1 in step, f"{st} {err}")

st, _, otp, _ = call(A + "RequestPasswordReset", msg((1, owner)))
st, err, step, _ = call(A + "ResetPassword", msg((1, s(otp, 1)), (2, code_for(owner)), (3, f"sanity long passphrase {RUN}")))
check("password reset by code, then signed in", st == 0 and 1 in step, f"{st} {err}")
st, err, pw, _ = call(A + "PasswordSignIn", msg((1, owner), (2, f"sanity long passphrase {RUN}")))
check("the new password works", st == 0 and 1 in pw, f"{st} {err}")
st, _, _, _ = call(F + "GetMyContext", b"", s(signed, 1))
check("the reset ended the earlier session", st == 16, str(st))

# The script keeps no device cookie, so that last sign-in came from a "new browser": a "New sign-in"
# email with a "This wasn't me" link.
report = re.search(r"/secure-account#([A-Za-z0-9_-]+)", mail_text(owner))
check("new browser → New sign-in email with a This-wasn't-me link", report is not None)
st, err, info, _ = call(A + "GetSignInReport", msg((1, report.group(1) if report else "")))
check("the link shows which sign-in it was", st == 0 and s(info, 1), f"{st} {err}")
st, err, _, _ = call(A + "SecureAccount", msg((1, report.group(1) if report else "")))
st2, _, _, _ = call(F + "GetMyContext", b"", s(pw.get(1, [b""])[0] and parse(pw[1][0]), 1)) if pw.get(1) else (16, None, None, None)
st3, _, _, _ = call(A + "PasswordSignIn", msg((1, owner), (2, f"sanity long passphrase {RUN}")))
check("This wasn't me: sessions end and the password stops working", st == 0 and st2 == 16 and st3 == 16, f"{st} {err} {st2} {st3}")

# ---------- slice 2.4: support access, guard tablets ----------
D = "estatehub.devices.v1.GuardDeviceService/"
E = "estatehub.estate.v1.EstateService/"

st, err, support, _ = call(A + "StartSupportAccess", msg((1, estates[0]), (2, "Smoke test: checking support access")), admin_token)
check("super admin opens read-only support access", st == 0 and s(support, 5) == "support", f"{st} {err}")
st, err, members_seen, _ = call(M + "ListMembers", b"", s(support, 1))
check("support can read estate 1 under RLS", st == 0 and len(members_seen.get(1, [])) >= 1, f"{st} {err}")
st, _, _, _ = call(E + "CreateBlock", msg((1, "Support was here")), s(support, 1))
check("support can't write", st == 7, str(st))
st, _, back, _ = call(A + "EndSupportAccess", b"", s(support, 1))
check("support access ends back at platform level", st == 0 and s(back, 5) == "super_admin" and s(back, 4) == "")

st, err, code, _ = call(D + "CreatePairingCode", msg((1, f"Gate {RUN}")), mgr_token)
check("manager creates a pairing code", st == 0 and re.fullmatch(r"[A-Z2-7]{4}-[A-Z2-7]{4}", s(code, 1)), f"{st} {err}")
st, err, paired, _ = call(D + "PairDevice", msg((1, s(code, 1))))
check("tablet pairs through the device lookup function (RLS)", st == 0 and DEVICE["cookie"], f"{st} {err}")
st, _, _, _ = call(D + "PairDevice", msg((1, s(code, 1))))
check("a pairing code works once", st == 16)

st, _, guard, _ = call(A + "DevSignIn", msg((1, f"sanity-guard-{RUN}"), (2, estates[0]), (3, "guard"), (4, True)))
st, err, _, _ = call(A + "SetGuardPin", msg((1, "482915")), s(guard, 1))
check("guard sets a PIN (RLS insert)", st == 0, f"{st} {err}")
st, err, tablet, _ = call(D + "GetTablet")
guards = [parse(g) for g in tablet.get(3, [])]
mine = next((g for g in guards if s(g, 2) == f"sanity-guard-{RUN}"), None)
check("tablet lists the guard with a PIN", st == 0 and mine is not None and mine.get(3) == [1], f"{st} {err}")
st, err, shift, _ = call(D + "GuardSignIn", msg((1, s(mine, 1) if mine else ""), (2, "482915")))
check("guard starts a shift with the PIN", st == 0 and s(shift, 5) == "guard", f"{st} {err}")

st, _, devices, _ = call(D + "ListDevices", b"", mgr_token)
device_id = s(parse(devices.get(1, [b""])[0]), 1) if devices.get(1) else ""
st, err, _, _ = call(D + "RevokeDevice", msg((1, device_id)), mgr_token)
st2, _, _, _ = call(F + "GetMyContext", b"", s(shift, 1))
st3, _, _, _ = call(D + "GetTablet")
check("revoking the tablet ends the shift and unpairs it", st == 0 and st2 == 16 and st3 == 16, f"{st} {err} {st2} {st3}")

# ---------- slice 2.5: passkeys (the full ceremony is in the C# integration tests) ----------
st, err, options, _ = call(A + "BeginPasskeySignIn")
request = json.loads(s(options, 2) or "{}")
check("passkey sign-in options for the web app's domain (stored on Postgres)",
      st == 0 and s(options, 1) and request.get("rpId") == "localhost" and request.get("userVerification") == "required", f"{st} {err} {request}")
st, err, _, _ = call(A + "FinishPasskeySignIn", msg((1, s(options, 1)), (2, '{"id":"bogus","type":"public-key"}')))
check("a bogus passkey answer is refused", st == 16, f"{st} {err}")

# ---------- Sprint 3.1: units ----------
def unit_input(section, quota, block=""):
    body = varint(1 << 3) + varint(section) + msg((4, quota), (3, block or None))
    return msg((1, body))

st, err, block, _ = call(E + "CreateBlock", msg((1, f"Block {RUN}")), mgr_token)
st1, err1, _, _ = call(E + "CreateUnit", unit_input(1, "60.5", s(block, 1)), mgr_token)
st2, err2, _, _ = call(E + "CreateUnit", unit_input(2, "39,5"), mgr_token)
st3, _, units, _ = call(E + "ListUnits", b"", mgr_token)
check("units with decimal quotas on Postgres (numeric), total 100%",
      st1 == 0 and st2 == 0 and st3 == 0 and s(units, 2) == "100.0000" and units.get(3) == [1], f"{err1} {err2} {s(units, 2)}")
st, _, other_units, _ = call(E + "ListUnits", b"", s(mgr2, 1))
check("estate 2 sees none of estate 1's units (RLS)", st == 0 and not other_units.get(1))
st, _, owner_signin, _ = call(A + "DevSignIn", msg((1, f"sanity-owner-{RUN}"), (2, estates[0]), (3, "owner"), (4, True)))
st, _, _, _ = call(E + "ListUnits", b"", s(owner_signin, 1))
check("owners can't list the estate's units", st == 7, str(st))

# ---------- Sprint 3.2: people, ownership, own units only ----------
P = "estatehub.people.v1.PeopleService/"
unit_ids = [s(parse(u), 1) for u in units.get(1, [])]
buyer = f"buyer-{RUN}@example.test"
st, err, detail, _ = call(P + "AddOwner", msg((1, unit_ids[0]), (2, msg((2, msg((1, "Smoke Buyer"), (2, buyer))))), (3, "100"), (4, "2020-01-01")), mgr_token)
person_id = s(parse(parse(detail.get(6, [b""])[0]).get(2, [b""])[0]), 1) if detail.get(6) else ""
check("owner added to a unit (people + ownership, RLS, dates)", st == 0 and s(detail, 8) == "100.00", f"{st} {err}")
st, err, invited, _ = call(P + "InvitePerson", msg((1, person_id)), mgr_token)
link = re.search(r"/invite#([A-Za-z0-9_-]+)", mail_text(buyer))
st2, _, otp2, _ = call(A + "StartInvitation", msg((1, link.group(1) if link else "")))
st3, err3, accepted2, _ = call(A + "AcceptInvitation", msg((1, s(otp2, 1)), (2, code_for(buyer))))
buyer_token = s(parse(accepted2.get(1, [b""])[0]), 1) if accepted2.get(1) else ""
st4, _, mine, _ = call(P + "MyUnits", b"", buyer_token)
mine_ids = [s(parse(u), 1) for u in mine.get(1, [])]
check("invited owner sees exactly their own unit (date filters on Postgres)", st == 0 and st3 == 0 and mine_ids == [unit_ids[0]], f"{st} {err} {st3} {err3} {mine_ids}")
st5, _, _, _ = call(P + "GetMyUnit", msg((1, unit_ids[1])), buyer_token)
check("...and not the neighbour's unit", st5 == 5, str(st5))

# ---------- Sprint 3.3: spreadsheet import (estate 2, which has no units yet) ----------
I = "estatehub.imports.v1.ImportService/"
mgr2_token = s(mgr2, 1)
st, err, template, _ = call(I + "GetImportTemplate", b"", mgr2_token)
check("import template is an .xlsx workbook", st == 0 and template.get(2, [b""])[0][:2] == b"PK", f"{st} {err}")
ann, ten = f"ann-{RUN}@example.test", f"ten-{RUN}@example.test"
csv = ("Section,PQ %,Block,Owner name,Owner email,Owner since,Tenant name,Tenant email\n"
       f"10,60,Smoke Block,Ann Owner,{ann},2020-01-01,,\n11,40,Smoke Block,Ann Owner,{ann},2020-01-01,Ten Ant,{ten}\n")
st, err, preview, _ = call(I + "PreviewImport", msg((1, "smoke.csv"), (2, csv.encode())), mgr2_token)
import_id = s(preview, 1)
summary = parse(preview.get(6, [b""])[0]) if preview.get(6) else {}
check("import preview stored (jsonb) and checked: 2 units, 1 new block, ready", st == 0 and s(preview, 3) == "ready" and summary.get(1) == [2] and summary.get(2) == [1], f"{st} {err} {s(preview, 3)}")
st, err, _, _ = call(I + "CommitImport", msg((1, import_id), (2, True)), mgr2_token)
batch = {}
for _ in range(100):
    _, _, batch, _ = call(I + "GetImport", msg((1, import_id)), mgr2_token)
    if s(batch, 3) not in ("queued", "running") and not batch.get(10):
        break
    time.sleep(0.1)
invites = parse(batch.get(13, [b""])[0]) if batch.get(13) else {}
_, _, units2, _ = call(E + "ListUnits", b"", mgr2_token)
check("import committed on Postgres (worker scope sets the estate for RLS): 2 units, quotas 100%",
      st == 0 and s(batch, 3) == "done" and len(units2.get(1, [])) == 2 and s(units2, 2) == "100.0000", f"{st} {err} {s(batch, 3)} {s(batch, 11)}")
check("import invitations sent to the owner and tenant", invites.get(2) == [2] and "invite#" in mail_text(ann) and "invite#" in mail_text(ten), f"{invites}")
st, _, _, _ = call(I + "GetImport", msg((1, import_id)), mgr_token)
check("estate 1 can't see estate 2's import (RLS)", st == 5, str(st))

# ---------- Sprint 3.4: resident portal + POPIA (the invited owner from 3.2) ----------
st, _, privacy, _ = call(E + "GetPrivacySettings", b"", mgr_token)
st2, err2, privacy2, _ = call(E + "UpdatePrivacySettings", msg((1, msg((1, "Smoke Officer"), (2, f"io-{RUN}@example.test")) + varint(4 << 3) + varint(120))), mgr_token)
check("privacy settings: default 90 days, saved on Postgres", st == 0 and privacy.get(4) == [90] and st2 == 0 and privacy2.get(4) == [120], f"{st} {st2} {err2}")
st, err, mine_details, _ = call(P + "GetMyDetails", b"", buyer_token)
version = s(mine_details, 7)
st2, _, acked, _ = call(P + "AcknowledgePrivacyNotice", msg((1, version)), buyer_token)
notice_text = " ".join(s(parse(n), 2) for n in mine_details.get(9, []))
check("resident reads the notice (names the Information Officer) and acknowledges it",
      st == 0 and mine_details.get(1) == [1] and "Smoke Officer" in notice_text and st2 == 0 and acked.get(8) == [1], f"{st} {err} {st2}")
st, err, export, _ = call(P + "ExportMyData", b"", buyer_token)
exported = json.loads(export.get(2, [b"{}"])[0] or b"{}")
check("resident downloads their own data (JSON with their unit)", st == 0 and exported.get("person", {}).get("email") == buyer and len(exported.get("owns", [])) == 1, f"{st} {err}")
owner_id = s(parse(detail.get(6, [b""])[0]), 1) if detail.get(6) else ""
st, err, _, _ = call(P + "EndOwnership", msg((1, owner_id), (2, "2024-12-31")), mgr_token)
st2, err2, _, _ = call(P + "AnonymisePerson", msg((1, person_id), (2, "Smoke Buyer")), mgr_token)
st3, _, _, _ = call(P + "GetMyDetails", b"", buyer_token)
st4, _, unit_after, _ = call(P + "GetUnit", msg((1, unit_ids[0])), mgr_token)
owner_after = parse(parse(unit_after.get(6, [b""])[0]).get(2, [b""])[0]) if unit_after.get(6) else {}
check("former owner anonymised on Postgres: history kept, account scrubbed, sessions ended",
      st == 0 and st2 == 0 and st3 == 16 and s(owner_after, 2).startswith("Former resident") and not s(owner_after, 3), f"{st} {err} {st2} {err2} {st3} {s(owner_after, 2)}")

# ---------- Sprint 4.1: billing setup, accounts, ledger (estate 2: the imported units) ----------
B = "estatehub.billing.v1.BillingService/"
AC = "estatehub.accounts.v1.AccountsService/"
st, err, settings, _ = call(B + "GetBillingSettings", b"", mgr2_token)
st2, _, charges, _ = call(B + "ListChargeTypes", b"", mgr2_token)
check("billing settings created with defaults and the built-in levy (Postgres)",
      st == 0 and settings.get(2) == [1] and settings.get(3) == [7] and any(parse(c).get(5) == [1] for c in charges.get(1, [])), f"{st} {err}")
st, err, bank, _ = call(B + "UpdateBankDetails", msg((1, "Smoke Bank"), (2, "123456"), (3, "Smoke Body Corporate"), (4, "1234567890")), mgr2_token)
check("bank details saved with a fresh MFA", st == 0 and s(bank, 12) == "1234567890", f"{st} {err}")
st, err, accs, _ = call(AC + "ListAccounts", b"", mgr2_token)
acc_items = [parse(a) for a in accs.get(1, [])]
check("imported owners got one account per unit (accounts follow ownership)",
      st == 0 and len(acc_items) == 2 and all(s(a, 5) == "Ann Owner" for a in acc_items), f"{st} {err} {len(acc_items)}")
acc_id = s(acc_items[0], 1) if acc_items else ""
st, err, opened, _ = call(AC + "RecordOpeningBalance", msg((1, acc_id), (2, "250.00"), (3, "2026-01-01")), mgr2_token)
entry_id = s(parse(opened.get(2, [b""])[0]), 1) if opened.get(2) else ""
st2, err2, reversed_, _ = call(AC + "ReverseEntry", msg((1, entry_id), (2, "smoke")), mgr2_token)
check("opening balance then reversal on Postgres: balance back to 0.00",
      st == 0 and s(parse(opened.get(1, [b""])[0]), 8) == "250.00" and st2 == 0 and s(parse(reversed_.get(1, [b""])[0]), 8) == "0.00", f"{st} {err} {st2} {err2}")
st, _, _, _ = call(AC + "GetAccount", msg((1, acc_id)), mgr_token)
check("estate 1 can't open estate 2's account (RLS + ownership check)", st in (5, 7), str(st))

# ---------- Sprint 4.2: a month posted on Postgres (estate 2: sections 10 and 11, quotas 60/40) ----------
R = "estatehub.billing.v1.BillingRunsService/"
month = time.strftime("%Y-%m")
st, err, _, _ = call(R + "AddBudget", msg((1, "2020-01"), (2, "1000.00"), (3, "250.00")), mgr2_token)
st2, err2, water, _ = call(B + "CreateChargeType", msg((1, msg((1, "Water"), (2, "metered"), (4, True)))), mgr2_token)
water_csv = "Unit,Amount,Reading\n10,123.45,W1\n11,99.99,W2\n"
st3, err3, uploaded, _ = call(R + "UploadMetered", msg((1, month), (2, s(water, 1)), (3, "water.csv"), (4, water_csv.encode())), mgr2_token)
check("budget, metered charge and upload saved (Postgres)", st == 0 and st2 == 0 and st3 == 0 and uploaded.get(1) == [1], f"{st} {err} {st2} {err2} {st3} {err3}")
st, err, run, _ = call(R + "CreateDraft", msg((1, month)), mgr2_token)
run_id = s(run, 1)
st2, err2, preview, _ = call(R + "PreviewRun", msg((1, run_id)), mgr2_token)
check("draft preview: 600 + 150 + 123.45 and 400 + 100 + 99.99 (quota split to the cent)",
      st == 0 and st2 == 0 and s(preview, 4) == "1473.44" and not preview.get(5), f"{st} {err} {st2} {err2} {s(preview, 4)} {[b.decode() for b in preview.get(5, [])]}")
st, err, _, _ = call(R + "PostRun", msg((1, run_id)), mgr2_token)
for _ in range(100):
    _, _, run, _ = call(R + "GetRun", msg((1, run_id)), mgr2_token)
    if s(run, 3) not in ("queued", "posting") and not run.get(9):
        break
    time.sleep(0.1)
_, _, accs2, _ = call(AC + "ListAccounts", b"", mgr2_token)
balances = sorted(s(parse(a), 8) for a in accs2.get(1, []))
check("run posted through the worker on Postgres (RLS + one transaction): balances match the preview",
      st == 0 and s(run, 3) == "posted" and s(run, 5) == "1473.44" and balances == ["599.99", "873.45"], f"{st} {err} {s(run, 3)} {s(run, 7)} {balances}")
st, err, _, _ = call(R + "PostRun", msg((1, run_id)), mgr2_token)
st2, _, again, _ = call(R + "CreateDraft", msg((1, month)), mgr2_token)
check("a month is posted once: posting again is refused, the month's run is the same", st == 3 and s(again, 1) == run_id, f"{st} {err}")

delete_run_mail()
print(f"\n{sum(ok for _, ok in results)}/{len(results)} passed (run {RUN})")
sys.exit(0 if all(ok for _, ok in results) else 1)
