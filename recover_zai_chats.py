#!/usr/bin/env python3
# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Z.ai Chat Recovery contributors
# Licensed under the Apache License, Version 2.0 (see LICENSE file).
"""
Z.ai chat recovery tool
=======================

For users whose chat.z.ai conversation list (sidebar) suddenly appeared EMPTY
(e.g. the frontend regression around 2026-10-16) while the conversations are
still stored on Z.ai's servers. This tool proves it and recovers everything:

  1. Finds your chat.z.ai session token in the local storage of any Chromium
     browser profile on this machine (Chrome / Edge / Brave / Chromium).
  2. Calls Z.ai's own read-only REST API:
       GET  /api/v1/chats                     -> full conversation list
       GET  /api/v1/chats/{id}                -> per-chat message tree
       POST /api/v1/chats/{id}/messages/batch -> message contents
  3. Writes to an output folder:
       INDEX.md            - all chats with links and message counts
       chat-urls.md        - direct https://chat.z.ai/c/<id> URLs
       <title>__<id>.md    - readable transcript per chat
       <id>.json           - raw API JSON (lossless)

Safety: READ-ONLY (never deletes or modifies anything on your account), makes
requests only to chat.z.ai, and your token never leaves this machine except in
requests to chat.z.ai itself. No third-party services, no telemetry.

Requirements: Python 3.8+ (standard library only), and a Chromium browser on
this computer where you are logged in to chat.z.ai.

Usage:
    python recover_zai_chats.py                     # auto: find token, recover all
    python recover_zai_chats.py --out my-backup     # choose output folder
    python recover_zai_chats.py --token eyJ...      # paste token manually
    python recover_zai_chats.py --limit 2           # quick test run

If auto-discovery finds nothing (or only "guest-..." identities): log in to
chat.z.ai in your browser once, close the tab, and rerun. Or extract the token
manually: chat.z.ai -> F12 -> Application -> Local Storage -> https://chat.z.ai
-> key "token" -> copy its value -> pass it with --token.
"""

import argparse
import base64
import datetime
import glob
import json
import os
import re
import sys
import time
import urllib.error
import urllib.request

API = "https://chat.z.ai/api/v1"
UA = "zai-chat-recovery/1.0 (local, read-only)"
JWT_RE = re.compile(
    r"eyJhbGciOi[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,}"
)
PT_OFFSET = datetime.timezone(datetime.timedelta(hours=-7))  # display only


# --------------------------------------------------------------------------
# token discovery
# --------------------------------------------------------------------------

def browser_roots():
    """Chromium browser profile roots likely to hold chat.z.ai local storage."""
    home = os.path.expanduser("~")
    la = os.environ.get("LOCALAPPDATA")
    if os.name == "nt" and la:
        cands = [
            (os.path.join(la, "Google", "Chrome"), "Chrome"),
            (os.path.join(la, "Google", "Chrome Beta"), "Chrome Beta"),
            (os.path.join(la, "Microsoft", "Edge"), "Edge"),
            (os.path.join(la, "BraveSoftware", "Brave-Browser"), "Brave"),
        ]
    elif sys.platform == "darwin":
        cands = [
            (home + "/Library/Application Support/Google/Chrome", "Chrome"),
            (home + "/Library/Application Support/Microsoft Edge", "Edge"),
            (home + "/Library/Application Support/BraveSoftware/Brave-Browser", "Brave"),
        ]
    else:
        cands += [
            (home + "/.config/google-chrome", "Chrome"),
            (home + "/.config/chromium", "Chromium"),
            (home + "/.config/microsoft-edge", "Edge"),
            (home + "/.config/BraveSoftware/Brave-Browser", "Brave"),
            (home + "/snap/chromium/common/chromium", "Chromium (snap)"),
        ]
    return [(p, n) for p, n in cands if os.path.isdir(p)]


def jwt_email(tok):
    try:
        p = tok.split(".")[1]
        payload = json.loads(base64.urlsafe_b64decode(p + "=" * (-len(p) % 4)))
        return payload.get("email") or "unknown"
    except Exception:
        return "unknown"


def find_token_candidates():
    """Scan Chromium local-storage leveldb files for JWT-shaped tokens.

    Returns candidates sorted best-first: real (non-guest) identities first,
    then by most recently written file and highest byte offset (latest write).
    """
    found = []
    for root, name in browser_roots():
        for prof in (glob.glob(os.path.join(root, "User Data", "*", "Local Storage", "leveldb"))
                     + glob.glob(os.path.join(root, "*", "Local Storage", "leveldb"))):
            profile = os.path.basename(os.path.dirname(os.path.dirname(prof)))
            for f in glob.glob(os.path.join(prof, "*")):
                base = os.path.basename(f)
                if not (base.endswith(".log") or base.endswith(".ldb")):
                    continue
                try:
                    data = open(f, "rb").read()
                except OSError:
                    continue
                hits = set()
                for blob in (data.decode("ascii", "ignore"),
                             data.decode("utf-16le", "ignore")):
                    hits |= {(m.start(), m.group()) for m in JWT_RE.finditer(blob)}
                for pos, tok in hits:
                    email = jwt_email(tok)
                    # z.ai tokens carry an email claim; skip foreign JWTs early
                    if email == "unknown":
                        continue
                    found.append({
                        "mtime": os.path.getmtime(f), "pos": pos, "token": tok,
                        "email": email, "where": f"{name}/{profile}/{base}",
                    })
    # Firefox keeps site localStorage in webappsstore.sqlite (unencrypted)
    for profiles_root in (
        os.path.join(os.environ.get("APPDATA", ""), "Mozilla", "Firefox", "Profiles"),
        os.path.expanduser("~/Library/Application Support/Firefox/Profiles"),
        os.path.expanduser("~/.mozilla/firefox"),
    ):
        if not os.path.isdir(profiles_root):
            continue
        for prof in os.listdir(profiles_root):
            db = os.path.join(profiles_root, prof, "webappsstore.sqlite")
            if not os.path.isfile(db):
                continue
            try:
                data = open(db, "rb").read()
            except OSError:
                continue
            hits = set()
            for blob in (data.decode("ascii", "ignore"), data.decode("utf-16le", "ignore")):
                hits |= {(m.start(), m.group()) for m in JWT_RE.finditer(blob)}
            for pos, tok in hits:
                email = jwt_email(tok)
                if email == "unknown":
                    continue
                found.append({
                    "mtime": os.path.getmtime(db), "pos": pos, "token": tok,
                    "email": email, "where": f"Firefox/{prof}/webappsstore.sqlite",
                })
    found.sort(key=lambda c: (c["email"].startswith("guest-"),
                             -c["mtime"], -c["pos"]))
    dedup, out = set(), []
    for c in found:
        if c["token"] in dedup:
            continue
        dedup.add(c["token"])
        out.append(c)
    return out


def validate_token(tok):
    """Return the chat list page 1 if the token works, else None."""
    try:
        return api("GET", "/chats/?page=1&size=100", tok)
    except urllib.error.HTTPError as e:
        if e.code in (401, 403):
            return None
        raise


# --------------------------------------------------------------------------
# api
# --------------------------------------------------------------------------

def api(method, path, tok, body=None):
    url = API + path
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data=data, method=method, headers={
        "Authorization": "Bearer " + tok,
        "User-Agent": UA,
        "Accept": "application/json",
        "Content-Type": "application/json",
    })
    last = None
    for attempt in (1, 2):
        try:
            with urllib.request.urlopen(req, timeout=30) as r:
                return json.loads(r.read().decode("utf-8", "replace"))
        except urllib.error.HTTPError as e:
            if e.code == 429 and attempt == 1:
                time.sleep(5)
                continue
            raise
        except Exception as e:  # transient network error: one retry
            last = e
            time.sleep(2)
    raise last


def list_all_chats(tok):
    chats, page = [], 1
    while True:
        batch = api("GET", f"/chats/?page={page}&size=100", tok)
        if isinstance(batch, dict):
            batch = (batch.get("items") or batch.get("data")
                     or batch.get("chats") or [])
        if not batch:
            break
        chats.extend(batch)
        if len(batch) < 100:
            break
        page += 1
        time.sleep(0.5)
    return chats


# --------------------------------------------------------------------------
# rendering
# --------------------------------------------------------------------------

def pt_time(v):
    try:
        if not v:
            return ""
        if v > 10 ** 12:
            v /= 1000
        return datetime.datetime.fromtimestamp(
            v, datetime.timezone.utc).astimezone(PT_OFFSET).strftime("%Y-%m-%d %H:%M")
    except Exception:
        return str(v)


def blocks_to_text(blocks):
    """Render content_blocks into (visible_text, thinking_text)."""
    vis, think = [], []
    for b in blocks if isinstance(blocks, list) else []:
        if isinstance(b, dict):
            t = b.get("type", "")
            txt = b.get("text") or b.get("content") or ""
            if not txt and t == "image_url":
                txt = "[image]"
            if not txt:
                continue
            if not isinstance(txt, str):
                txt = json.dumps(txt, ensure_ascii=False)[:400]
            (think if t == "thinking" else vis).append(txt)
    return "\n\n".join(vis), "\n\n".join(think)


def linearize(messages):
    """Order message ids root->leaf, preferring the latest branch at forks."""
    children = {}
    for k, m in messages.items():
        children.setdefault(m.get("parentId"), []).append(k)

    def walk(node, acc):
        acc.append(node)
        kids = children.get(node) or []
        if kids:
            walk(kids[-1], acc)
        return acc

    roots = children.get(None) or [k for k, m in messages.items()
                                   if not m.get("parentId")]
    order = []
    for r in roots or []:
        if r in messages:
            order += walk(r, [])
    return order or list(messages)


def safe_name(title):
    return re.sub(r"[^\w\s.-]", "", title or "untitled")[:50].strip().replace(" ", "_")


def render_chat(chat_meta, detail, outdir):
    messages = (detail.get("chat") or {}).get("history", {}).get("messages", {})
    order = linearize(messages)
    cid = chat_meta["id"]
    fname = f"{safe_name(chat_meta.get('title'))}__{cid[:8]}.md"
    n = 0
    with open(os.path.join(outdir, fname), "w", encoding="utf-8") as fh:
        fh.write(f"# {chat_meta.get('title')}\n\n")
        fh.write(f"- id: {cid}\n")
        fh.write(f"- direct url: https://chat.z.ai/c/{cid}\n")
        fh.write(f"- created: {pt_time(chat_meta.get('created_at'))}\n")
        fh.write(f"- updated: {pt_time(chat_meta.get('updated_at'))}\n\n---\n\n")
        for k in order:
            m = messages.get(k, {})
            cm = m.get("content_detail") or {}
            blocks = cm.get("content_blocks") or m.get("content_blocks") or []
            vis, think = blocks_to_text(blocks)
            if not vis and isinstance(m.get("content"), str):
                vis = m["content"]
            if not vis and not think:
                continue
            n += 1
            tstamp = pt_time(m.get("timestamp"))
            fh.write(f"## {(m.get('role') or '?').upper()}"
                     f"{'  (' + tstamp + ')' if tstamp else ''}\n\n")
            if think:
                fh.write(f"<details><summary>thinking</summary>\n\n{think[:4000]}\n\n</details>\n\n")
            if vis:
                fh.write(vis + "\n\n")
    return fname, n


# --------------------------------------------------------------------------
# main
# --------------------------------------------------------------------------

def main():
    ap = argparse.ArgumentParser(description="Recover chat.z.ai conversations "
                                             "when the sidebar list is empty.")
    ap.add_argument("--token", help="session token (JWT) if auto-discovery fails")
    ap.add_argument("--out", default=None, help="output folder (default: zai-chat-recovery-<date>)")
    ap.add_argument("--limit", type=int, default=0, help="recover only the N most recent chats (testing)")
    args = ap.parse_args()

    print("Z.ai chat recovery (read-only). Requests go to chat.z.ai only.\n")

    # --- 1. obtain a working token -----------------------------------------
    tok, email = args.token, None
    if not tok:
        print("Looking for a chat.z.ai session token in local browser storage...")
        cands = find_token_candidates()
        if not cands:
            print("  no candidate tokens found in any Chromium browser profile.")
        for c in cands[:8]:
            guest = " [GUEST session - not your real account]" \
                if c["email"].startswith("guest-") else ""
            print(f"  trying: {c['email']}  ({c['where']}){guest}")
            if c["email"].startswith("guest-"):
                continue
            if validate_token(c["token"]) is not None:
                tok, email = c["token"], c["email"]
                break
        if not tok and cands:
            print("\nOnly guest/invalid tokens found. Log in to chat.z.ai in your"
                  " browser (fresh login), then rerun this script.")
            print("Or extract the token manually: chat.z.ai -> F12 -> Application"
                  " -> Local Storage -> https://chat.z.ai -> key \"token\".")
            return 2
        if not tok:
            print("\nFallback: open chat.z.ai in your browser, press F12 ->"
                  " Application -> Local Storage -> https://chat.z.ai -> key"
                  " \"token\", copy the value, and rerun with --token <value>.")
            return 2
    else:
        email = jwt_email(tok)
        if validate_token(tok) is None:
            print("The provided token was rejected (401/403). Log in to chat.z.ai"
                  " in the browser and get a fresh token.")
            return 2

    print(f"\nToken OK. Account: {email if email != 'unknown' else '(email not in token)'}")

    # --- 2. list + fetch ----------------------------------------------------
    chats = list_all_chats(tok)
    if not chats:
        print("\nThe API returned an EMPTY list. If you did have conversations,"
              " they may live under a different login method (e.g. another Google"
              " account). Log in to chat.z.ai with that account in the browser,"
              " then rerun.")
        return 3
    chats.sort(key=lambda c: c.get("updated_at") or 0, reverse=True)
    if args.limit:
        chats = chats[:args.limit]
    print(f"Found {len(chats)} conversations. Fetching contents "
          f"(about {1 + 2 * len(chats)} requests, gentle pacing)...")

    outdir = args.out or ("zai-chat-recovery-"
                          + datetime.datetime.now().strftime("%Y%m%d-%H%M%S"))
    os.makedirs(outdir, exist_ok=True)

    index_lines, url_lines, done, failed = [], [], 0, 0
    for c in chats:
        cid = c["id"]
        try:
            detail = api("GET", f"/chats/{cid}", tok)
            messages = (detail.get("chat") or {}).get("history", {}).get("messages", {})
            ids = list(messages)
            for i in range(0, len(ids), 100):
                r = api("POST", f"/chats/{cid}/messages/batch", tok,
                        {"ids": ids[i:i + 100]})
                data = (r or {}).get("data") or {}
                for k, cm in data.items():
                    if k in messages:
                        messages[k]["content_detail"] = cm
                time.sleep(0.4)
            with open(os.path.join(outdir, f"{cid}.json"), "w", encoding="utf-8") as fh:
                json.dump(detail, fh, indent=2, ensure_ascii=False)
            fname, n = render_chat(c, detail, outdir)
            done += 1
            t = pt_time(c.get("updated_at"))
            index_lines.append(f"- [{c.get('title')}]({fname}) - {n} messages, updated {t}")
            url_lines.append(f"| {len(index_lines)} | {c.get('title')} | {t} "
                             f"| https://chat.z.ai/c/{cid} |")
            print(f"  [{done}/{len(chats)}] {str(c.get('title'))[:50]} ({n} messages)")
        except Exception as e:
            failed += 1
            print(f"  FAILED {cid[:8]}: {str(e)[:80]}")
        time.sleep(0.4)

    with open(os.path.join(outdir, "INDEX.md"), "w", encoding="utf-8") as fh:
        fh.write(f"# Recovered z.ai conversations ({done} chats)\n\n"
                 f"Account: {email}\nRecovered: {datetime.datetime.now(PT_OFFSET):%Y-%m-%d %H:%M} PT\n\n"
                 + "\n".join(index_lines) + "\n")
    with open(os.path.join(outdir, "chat-urls.md"), "w", encoding="utf-8") as fh:
        fh.write("# Direct chat URLs\n\n| # | Title | Updated | URL |\n|---|---|---|---|\n"
                 + "\n".join(url_lines) + "\n")

    print(f"\nDone. {done} chats recovered, {failed} failed.")
    print(f"Output folder: {os.path.abspath(outdir)}")
    print("Open INDEX.md first. chat-urls.md has the direct URLs - these work in")
    print("your browser even while the sidebar is broken.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
