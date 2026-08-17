#!/usr/bin/env python3
"""Assert that CI's live proof still covers every server line the harness has.

`scripts/gap/gap.sh` with no arguments runs every supported line. CI runs one
job per line instead, so that a failure on 10.11.11 does not leave 12.0
untested — which means the list of lines now lives in two files, and nothing
about the second one is checked by running the first.

The failure that follows is quiet in the way that matters. Add a line to the
harness — 12.0 stable, when it arrives — and forget the matrix, and every
workflow stays green, the summary still says the live proof passed, and the new
server is simply never started. Nobody is told a line is missing, because from
CI's point of view nothing is: the jobs it knows about all succeeded.

Also checked here, though it fails loudly rather than quietly: the workflow
derives each server's tarball name out of gap.sh to key its cache, and a line
with no tarball pinned would take the job down. Better to hear about that from
a two-second lint step than from a job that queued, installed ffmpeg, and then
gave up.
"""

import pathlib
import re
import sys

import yaml

REPO = pathlib.Path(__file__).resolve().parents[2]
HARNESS = REPO / "scripts" / "gap" / "gap.sh"
WORKFLOW = pathlib.Path(__file__).with_name("live-gap.yml")

# SERVER_LINES=(10.11.11 12.0-rc5), wherever the harness spells one out. Not
# anchored to the start of a line: both of them sit behind something else, one
# in a case arm and one behind a `||`. The word boundary is what keeps
# SERVER_LINES+=("$1") out — that one appends the argument it was given and says
# nothing about which lines exist. The empty declaration is skipped for the same
# reason: it introduces the variable rather than describing the servers.
ASSIGNMENT = re.compile(r"\bSERVER_LINES=\(([^)]*)\)")


def main() -> int:
    harness = HARNESS.read_text(encoding="utf-8")
    workflow = yaml.safe_load(WORKFLOW.read_text(encoding="utf-8"))

    listed = [frozenset(match.split()) for match in ASSIGNMENT.findall(harness) if match.split()]
    if not listed:
        return fail(HARNESS, "gap.sh lists no server lines at all; has SERVER_LINES been renamed?")

    # `both` and the bare-invocation default are two separate literals in the
    # harness. If they have drifted apart there is no single answer to compare
    # the matrix against, and one of the two is already wrong.
    if len(set(listed)) != 1:
        return fail(
            HARNESS,
            "gap.sh's SERVER_LINES literals disagree: "
            + " vs ".join(sorted(" ".join(sorted(entry)) for entry in set(listed))),
        )

    supported = set(listed[0])

    # Reached by hand rather than by subscript, so that renaming the job or
    # reshaping the matrix produces something a reader can act on instead of a
    # KeyError traceback. A check that fails unreadably gets deleted.
    matrix = workflow
    for key in ("jobs", "prove", "strategy", "matrix", "line"):
        if not isinstance(matrix, dict) or key not in matrix:
            return fail(
                WORKFLOW,
                f"live-gap.yml has no jobs.prove.strategy.matrix.line (stopped at '{key}'), "
                "so there is no list of server lines to check the harness against",
            )
        matrix = matrix[key]

    if not isinstance(matrix, list) or not matrix:
        return fail(WORKFLOW, f"the job matrix lists no server lines: {matrix!r}")

    covered = {str(line) for line in matrix}

    missing = supported - covered
    if missing:
        return fail(
            WORKFLOW,
            f"the live proof never runs {', '.join(sorted(missing))} — "
            "gap.sh supports the line and the job matrix does not list it, so CI is green "
            "on a server nobody started",
        )

    extra = covered - supported
    if extra:
        return fail(
            WORKFLOW,
            f"the matrix runs {', '.join(sorted(extra))}, which gap.sh does not support; "
            "the job would fail at 'no server line is defined'",
        )

    # The same derivation the workflow's cache step performs, checked here so it
    # is a lint failure rather than a job that dies after doing real work.
    for line in sorted(covered):
        if not re.search(rf"jellyfin_{re.escape(line)}[^|\"]*\.tar\.gz", harness):
            return fail(
                HARNESS,
                f"no server tarball is pinned for {line}, so the live proof cannot cache "
                "or download one",
            )

    print("the live proof covers every line gap.sh supports:", " ".join(sorted(covered)))
    return 0


def fail(path: pathlib.Path, message: str) -> int:
    print(f"::error file={path.relative_to(REPO)}::{message}", file=sys.stderr)
    return 1


if __name__ == "__main__":
    sys.exit(main())
