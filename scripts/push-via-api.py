#!/usr/bin/env python3
"""Push a directory to GitHub through the REST API instead of `git push`.

Why this exists: on some networks github.com:443 (the git transport) is blocked while
api.github.com works. The Git Data API can build the same commit server-side, so the
repository stays publishable without a proxy.

Usage:  python build/push-via-api.py [--dir ../ECL-public] [--repo owner/name] [--dry-run]
Token:  read from build/token.txt
"""

import argparse
import base64
import json
import os
import sys
import urllib.error
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def token():
    path = os.path.join(ROOT, "build", "token.txt")
    with open(path, encoding="utf-8") as fh:
        return fh.read().strip()


def api(method, path, payload=None, tok=None):
    url = "https://api.github.com" + path
    data = json.dumps(payload).encode("utf-8") if payload is not None else None
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("Authorization", "Bearer " + tok)
    req.add_header("Accept", "application/vnd.github+json")
    req.add_header("User-Agent", "ecl-push")
    if data:
        req.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(req, timeout=60) as resp:
            body = resp.read()
            return json.loads(body) if body else {}
    except urllib.error.HTTPError as exc:
        print(f"  HTTP {exc.code} on {method} {path}: {exc.read()[:300]!r}")
        raise


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dir", default=os.path.join(ROOT, "..", "ECL-public"))
    ap.add_argument("--repo", default="ChenchenMC-cn/EternityCraftLauncher")
    ap.add_argument("--branch", default="main")
    ap.add_argument("--message", default="sync " + __import__("datetime").date.today().isoformat())
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args()

    tok = token()
    src = os.path.abspath(args.dir)
    if not os.path.isdir(src):
        sys.exit("not a directory: " + src)

    files = []
    for base, dirs, names in os.walk(src):
        dirs[:] = [d for d in dirs if d != ".git"]
        for name in names:
            full = os.path.join(base, name)
            rel = os.path.relpath(full, src).replace("\\", "/")
            files.append((rel, full))
    files.sort()
    print(f"repo   : {args.repo} ({args.branch})")
    print(f"source : {src}")
    print(f"files  : {len(files)}")
    if args.dry_run:
        for rel, _ in files[:10]:
            print("   ", rel)
        return

    # 1. blobs
    tree = []
    for rel, full in files:
        with open(full, "rb") as fh:
            raw = fh.read()
        sha = api("POST", f"/repos/{args.repo}/git/blobs", {
            "content": base64.b64encode(raw).decode("ascii"),
            "encoding": "base64",
        }, tok)["sha"]
        tree.append({"path": rel, "mode": "100644", "type": "blob", "sha": sha})
        print(f"  blob {rel}")

    # 2. tree (no base_tree: the published tree mirrors this directory exactly)
    tree_sha = api("POST", f"/repos/{args.repo}/git/trees", {"tree": tree}, tok)["sha"]

    # 3. commit on top of the current head
    ref = api("GET", f"/repos/{args.repo}/git/ref/heads/{args.branch}", None, tok)
    parent = ref["object"]["sha"]
    commit_sha = api("POST", f"/repos/{args.repo}/git/commits", {
        "message": args.message,
        "tree": tree_sha,
        "parents": [parent],
    }, tok)["sha"]

    # 4. move the branch
    api("PATCH", f"/repos/{args.repo}/git/refs/heads/{args.branch}",
        {"sha": commit_sha, "force": False}, tok)

    print(f"pushed {len(tree)} files -> {commit_sha[:7]} on {args.branch}")


if __name__ == "__main__":
    main()
