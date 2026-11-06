"""Register a supplier, connect a tenant, submit an idempotent notification, and poll it."""
import json
import os
import time
import urllib.request
import uuid

base = os.environ.get("HUB_URL", "http://localhost:8080")
supplier = os.environ.get("SUPPLIER_URL", "http://mock:8081/flaky")


def call(method, path, key, body=None, event_key=None):
    headers = {"X-Api-Key": key, "Content-Type": "application/json", "Accept-Language": "en"}
    if event_key:
        headers["Idempotency-Key"] = event_key
    request = urllib.request.Request(base + path, data=None if body is None else json.dumps(body).encode(), headers=headers, method=method)
    with urllib.request.urlopen(request) as response:
        result = response.read()
        return json.loads(result) if result else None


def main():
    provider_key = os.environ["PROVIDER_API_KEY"]
    business_key = os.environ["BUSINESS_API_KEY"]
    provider = call("POST", "/v1/providers", provider_key, {"name": "Demo CRM"})
    definition = {
        "eventType": "contact.updated.v1", "url": supplier, "method": "POST", "encoding": "json", "headers": {},
        "body": {"customerId": {"source": "data.contact_id"}, "state": {"source": "data.status", "values": {"paid": 1}}},
        "inputs": [{"name": "contact_id", "type": "string", "required": True}, {"name": "status", "type": "string", "required": True}],
        "success": {"httpStatuses": [200], "bodyPath": "code", "expectedValue": 0},
        "maxAttempts": 3, "timeoutSeconds": 5
    }
    version = call("POST", f"/v1/providers/{provider['id']}/operations/contact.update/versions", provider_key, definition)
    call("POST", f"/v1/providers/{provider['id']}/versions/{version['id']}/publish", provider_key)
    connection = call("POST", "/v1/connections", business_key, {"providerId": provider["id"], "name": "Demo connection"})
    call("POST", "/v1/subscriptions", business_key, {"url": supplier.rsplit('/', 1)[0] + "/events", "signingSecret": "local-demo-signing-secret-32-characters"})
    body = {"eventType": "contact.updated.v1", "targets": [{"connectionId": connection["id"], "operation": "contact.update"}], "data": {"contact_id": "c123", "status": "paid"}}
    key = str(uuid.uuid4())
    receipt = call("POST", "/v1/notifications", business_key, body, key)
    duplicate = call("POST", "/v1/notifications", business_key, body, key)
    assert receipt["id"] == duplicate["id"] and duplicate["duplicate"]
    print(json.dumps(receipt, indent=2))
    for _ in range(90):
        view = call("GET", f"/v1/notifications/{receipt['id']}", business_key)
        states = [delivery["status"] for delivery in view["deliveries"]]
        print("Delivery states:", states)
        if all(state in ("succeeded", "stopped", "expired", "cancelled") for state in states):
            print(json.dumps(view, indent=2))
            return
        time.sleep(2)
    raise RuntimeError("The demo did not finish within three minutes.")


if __name__ == "__main__":
    main()
