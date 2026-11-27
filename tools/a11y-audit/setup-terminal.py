# Prepares a freshly reset demo for the accessibility audit (#142, D-127), and puts it back afterwards.
#
#   WORKING=<a password> python3 setup-terminal.py         admin's temporary password -> WORKING (it allows nothing else); a terminal A11Y on
#                                                          EXT-01; admin given a person and badge A11Y-BADGE-1. Prints the enrollment code.
#   WORKING=<same> python3 setup-terminal.py code          a new enrollment code for A11Y (each is used once).
#   WORKING=<same> python3 setup-terminal.py restore       admin back on the demo's temporary password, as the demo setup leaves it.
import json, os, sys, urllib.request

API = os.environ.get("API", "http://localhost:5080/api")
TEMPORARY = os.environ.get("TEMPORARY", "Pulaski-demo-1")
WORKING = os.environ["WORKING"]


def call(method, path, body=None, token=None):
    req = urllib.request.Request(API + path, method=method, data=json.dumps(body).encode() if body is not None else None,
                                 headers={"Content-Type": "application/json", **({"Authorization": "Bearer " + token} if token else {})})
    with urllib.request.urlopen(req) as r:
        text = r.read()
        return json.loads(text) if text else None


def sign_in(password):
    return call("POST", "/auth/sign-in", {"username": "admin", "password": password})["token"]


def terminal(token):
    site = next(e for e in call("GET", "/equipment", token=token) if e["code"] == "PLT1")["id"]
    return site, next((t for t in call("GET", f"/terminals?siteId={site}", token=token) if t["code"] == "A11Y"), None)


mode = sys.argv[1] if len(sys.argv) > 1 else "setup"
if mode == "setup":
    call("POST", "/auth/password", {"currentPassword": TEMPORARY, "newPassword": WORKING}, sign_in(TEMPORARY))
    token = sign_in(WORKING)
    equipment = {e["code"]: e for e in call("GET", "/equipment", token=token)}
    site, line = equipment["PLT1"]["id"], equipment["EXT-01"]["id"]
    created = call("POST", "/terminals", {"siteId": site, "code": "A11Y", "name": "Accessibility audit", "scopeEquipmentId": site,
                                          "defaultEquipmentId": line, "authMode": "badge"}, token)
    person = call("POST", "/people", {"siteId": site, "employeeNumber": "A11Y-1", "displayName": "Audit Admin", "username": "admin"}, token)
    call("POST", "/badges", {"personId": person["id"], "badgeValue": "A11Y-BADGE-1"}, token)
    print(call("POST", f"/terminals/{created['id']}/enrolment-code", {}, token)["code"])
elif mode == "code":
    token = sign_in(WORKING)
    print(call("POST", f"/terminals/{terminal(token)[1]['id']}/enrolment-code", {}, token)["code"])
elif mode == "restore":
    token = sign_in(WORKING)
    call("POST", f"/users/{call('GET', '/me', token=token)['id']}/password", {"temporaryPassword": TEMPORARY}, token)
    print("admin is back on the temporary password")
