#!/usr/bin/env python3
"""Assert that the release workflow still gives every commit its own release.

The invariant is three settings deep and each of them fails quietly:

  group               serializes the runs, so two pushes cannot both derive a
                      version from the same latest tag
  cancel-in-progress  false, so a newer push does not kill the release that is
                      already building
  queue               max, because cancel-in-progress protects only the
                      *running* workflow. The pending slot defaults to
                      queue: single, which cancels the waiting run to make room
                      for a newer one - so under three quick merges the middle
                      commit is silently released by nobody.

None of that is visible after the fact. A cancelled pending run leaves no
release, and an absent release is exactly what a missing run looks like, so
there is nothing to notice. Hence a check rather than a comment.
"""

import pathlib
import sys

import yaml

WORKFLOW = pathlib.Path(__file__).with_name("release.yml")

EXPECTED = {
    "cancel-in-progress": False,
    "queue": "max",
}


def main() -> int:
    concurrency = yaml.safe_load(WORKFLOW.read_text(encoding="utf-8")).get("concurrency")

    if not isinstance(concurrency, dict):
        return fail("release.yml has no concurrency block, so two releases can race for one version")

    if not concurrency.get("group"):
        return fail("concurrency.group is missing, so releases no longer serialize")

    for key, expected in EXPECTED.items():
        actual = concurrency.get(key)
        if actual != expected:
            return fail(
                f"concurrency.{key} is {actual!r}, expected {expected!r} - "
                "a commit can be released without its own run"
            )

    print("release.yml queues every pending run:", concurrency)
    return 0


def fail(message: str) -> int:
    print(f"::error file=.github/workflows/release.yml::{message}", file=sys.stderr)
    return 1


if __name__ == "__main__":
    sys.exit(main())
