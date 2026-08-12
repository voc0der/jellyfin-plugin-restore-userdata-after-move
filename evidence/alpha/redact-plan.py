#!/usr/bin/env python3
"""Publish a redacted copy of a plan, without claiming its plan ID.

A plan ID is a SHA-256 over the plan's own contents, so replacing the disposable
server's absolute paths with <SCRATCH> invalidates it. A published copy that
still carried the original `planId` would be asserting an integrity check that
fails on the file you are holding.

This drops `planId` and records provenance instead: the original ID, and the
SHA-256 of the original file's bytes, so anyone holding that file can confirm it
is the one this copy came from. The published copy is explicitly a derived
artifact and is meant to fail verification.

    ./redact-plan.py <plan.json> <output.json> <scratch-prefix> [<scratch-prefix>...]
"""

import hashlib
import json
import sys


def main() -> int:
    if len(sys.argv) < 4:
        print(__doc__, file=sys.stderr)
        return 2

    source, destination, prefixes = sys.argv[1], sys.argv[2], sys.argv[3:]

    raw = open(source, "rb").read()
    original_sha256 = hashlib.sha256(raw).hexdigest()

    text = raw.decode("utf-8")
    for prefix in prefixes:
        text = text.replace(prefix, "<SCRATCH>")

    plan = json.loads(text)
    original_plan_id = plan.pop("planId", None)
    plan["redaction"] = {
        "note": (
            "Derived artifact. Absolute paths of the disposable test server were "
            "replaced with <SCRATCH>, which changes the hashed content, so this "
            "copy deliberately carries no planId and will not pass "
            "PlanCanonicalizer.VerifyPlanId. The unmodified plan the server wrote "
            "does verify."
        ),
        "originalPlanId": original_plan_id,
        "originalFileSha256": original_sha256,
        "replaced": prefixes,
    }

    with open(destination, "w", encoding="utf-8") as out:
        json.dump(plan, out, indent=2)
        out.write("\n")

    print(f"{destination}  (from planId {original_plan_id[:12]}, file sha256 {original_sha256[:12]})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
