#!/usr/bin/env python3
"""Rebuild the bundled Open Fixture Library snapshot that the DMX fixture picker searches.

Usage (from the repo root):
    python3 src/Utilities/update-ofl-snapshot.py [<commit-sha>]

Without a commit it uses the current tip of OFL's master branch. It downloads that commit's
fixture files, packs them into one minified, gzipped JSON file, and copies OFL's MIT licence
next to it (the licence must ship with the data). Commit both files in a normal PR.

Output: src/Lanyard.Server/LanyardServices/Services/Dmx/Ofl/
    ofl-snapshot.json.gz              {"source": {...}, "manufacturers": {...}, "fixtures": {"<man>/<key>": {...}}}
    open-fixture-library-LICENSE.txt
"""

import gzip
import io
import json
import sys
import tarfile
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

REPO = "OpenLightingProject/open-fixture-library"
OUT_DIR = Path(__file__).resolve().parents[1] / "Lanyard.Server" / "LanyardServices" / "Services" / "Dmx" / "Ofl"


def fetch(url: str) -> bytes:
    request = urllib.request.Request(url, headers={"User-Agent": "Lanyard-ofl-snapshot"})
    with urllib.request.urlopen(request, timeout=120) as response:
        return response.read()


def main() -> None:
    commit = sys.argv[1] if len(sys.argv) > 1 else json.loads(fetch(f"https://api.github.com/repos/{REPO}/commits/master"))["sha"]
    print(f"Downloading {REPO} at {commit}...")
    archive = tarfile.open(fileobj=io.BytesIO(fetch(f"https://codeload.github.com/{REPO}/tar.gz/{commit}")), mode="r:gz")

    manufacturers: dict = {}
    fixtures: dict = {}
    licence = None

    for member in archive.getmembers():
        if not member.isfile():
            continue

        # "<repo>-<sha>/fixtures/<manufacturer>/<fixture>.json"
        parts = member.name.split("/", 1)[1].split("/")
        data = archive.extractfile(member).read()

        if parts == ["LICENSE"]:
            licence = data
        elif parts == ["fixtures", "manufacturers.json"]:
            manufacturers = {key: {"name": value["name"]} for key, value in json.loads(data).items() if not key.startswith("$")}
        elif len(parts) == 3 and parts[0] == "fixtures" and parts[2].endswith(".json"):
            fixture = json.loads(data)
            fixture.pop("$schema", None)
            fixture.pop("links", None)  # manual/product URLs aren't used, and are most of the bulk
            fixtures[f"{parts[1]}/{parts[2][:-5]}"] = fixture

    if licence is None or not fixtures:
        sys.exit("The download didn't contain fixtures and a LICENSE; nothing written.")

    snapshot = {
        "source": {
            "repository": f"https://github.com/{REPO}",
            "commit": commit,
            "builtAt": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        },
        "manufacturers": dict(sorted(manufacturers.items())),
        "fixtures": dict(sorted(fixtures.items())),
    }

    OUT_DIR.mkdir(parents=True, exist_ok=True)
    payload = json.dumps(snapshot, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
    # mtime=0 keeps the file byte-identical when nothing changed, so git sees no diff.
    with open(OUT_DIR / "ofl-snapshot.json.gz", "wb") as raw, gzip.GzipFile(fileobj=raw, mode="wb", compresslevel=9, mtime=0) as out:
        out.write(payload)
    (OUT_DIR / "open-fixture-library-LICENSE.txt").write_bytes(licence)

    size_kb = (OUT_DIR / "ofl-snapshot.json.gz").stat().st_size // 1024
    print(f"Wrote {len(fixtures)} fixtures from {len(manufacturers)} manufacturers ({size_kb} KB) to {OUT_DIR}")


if __name__ == "__main__":
    main()
