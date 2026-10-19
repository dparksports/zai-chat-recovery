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
       reopen-chats.html   - one-click page: every chat still opens on the
                             website (open it in the browser logged in to z.ai)
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
    python recover_zai_chats.py --open 10           # also reopen the 10 newest
                                                    # chats in your default browser

If auto-discovery finds nothing (or only "guest-..." identities): log in to
chat.z.ai in your browser once, close the tab, and rerun. Or extract the token
manually: chat.z.ai -> F12 -> Application -> Local Storage -> https://chat.z.ai
-> key "token" -> copy its value -> pass it with --token.
"""

import argparse
import base64
import datetime
import glob
import html
import json
import os
import re
import string
import struct
import sys
import time
import urllib.error
import urllib.request
import webbrowser

API = "https://chat.z.ai/api/v1"
UA = "zai-chat-recovery/1.0 (local, read-only)"
JWT_RE = re.compile(
    r"eyJhbGciOi[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,}"
)
PT_OFFSET = datetime.timezone(datetime.timedelta(hours=-7))  # display only

# Self-contained reopen page: no network, no dependencies, big calm type.
# Batch opens happen synchronously inside one click because browsers only
# allow window.open during a user gesture - paced/delayed opens get blocked.
REOPEN_PAGE = string.Template("""<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Your chats still open - Z.ai Chat Recovery</title>
<style>
  :root { --bg:#F5F5F7; --panel:#FFFFFF; --line:#E5E5EA; --text:#1D1D1F; --muted:#6E6E73;
          --accent:#007AFF; --good:#1E8E3E; --warn:#B45309; }
  * { box-sizing:border-box; }
  html { -webkit-text-size-adjust:100%; }
  body { margin:0; padding:0 20px 64px; background:var(--bg); color:var(--text);
         font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,Ubuntu,Cantarell,"Noto Sans","Helvetica Neue",Arial,sans-serif;
         font-size:18px; line-height:1.55; }
  .wrap { max-width:820px; margin:0 auto; }
  h1 { font-size:30px; line-height:1.25; margin:38px 0 8px; font-weight:800; }
  .sub { font-size:19px; color:var(--muted); margin:0 0 20px; }
  .banner { background:#E5F1FF; border:1px solid #A8CFFF; border-radius:12px;
            padding:14px 18px; margin:0 0 20px; font-size:17px; }
  .brow { display:flex; flex-wrap:wrap; gap:12px; align-items:center; margin:0 0 14px; }
  button.big { min-height:52px; padding:12px 24px; font-size:18px; font-weight:600;
               color:#fff; background:var(--accent); border:0; border-radius:12px; cursor:pointer; }
  button.big:disabled { opacity:.4; cursor:default; }
  .progress { font-size:16.5px; color:var(--muted); }
  #q { display:block; width:100%; min-height:52px; font-size:18px; padding:10px 16px;
       border:1.5px solid var(--line); border-radius:12px; background:#fff; color:var(--text);
       font-family:inherit; }
  ol.chats { list-style:none; margin:14px 0 0; padding:0; }
  li.chat { display:flex; gap:14px; align-items:center; background:var(--panel);
            border:1px solid var(--line); border-radius:14px; padding:14px 18px; margin-bottom:10px; }
  li.chat .meta { flex:1; min-width:0; }
  li.chat .t { font-size:18.5px; font-weight:600; overflow-wrap:anywhere; }
  li.chat .d { font-size:15px; color:var(--muted); margin-top:3px; }
  li.chat .d .old { color:var(--warn); font-weight:600; }
  a.open { flex-shrink:0; display:inline-block; min-height:48px; padding:10px 20px;
           font-size:17px; font-weight:600; color:var(--accent); background:#fff;
           border:2px solid var(--accent); border-radius:12px; text-decoration:none; }
  a.open:visited { color:var(--accent); }
  li.opened { opacity:.75; }
  li.opened a.open { color:var(--good); border-color:var(--good); }
  .tip { display:none; background:#FFF4E0; border:1px solid #E8C57E; color:var(--warn);
         border-radius:12px; padding:12px 16px; font-size:16px; margin-top:14px; }
  .tip.show { display:block; }
  footer { margin-top:36px; font-size:14.5px; color:var(--muted); }
</style>
</head>
<body>
<div class="wrap">
  <h1>Your chats still open</h1>
  <p class="sub">$COUNTLABEL, saved on this computer. Every one is still on the z.ai website -
     even if your chat list there looks empty. Click any chat to read it.</p>
  <div class="banner"><strong>One thing first:</strong> this page uses your browser's own z.ai login.
     Make sure the browser showing this page is signed in as $ACCOUNT -
     if a chat asks you to sign in, sign in, then come back. Nothing is lost by closing this page.</div>
  <div class="brow">
    <button class="big" id="next" type="button">Open the next 10 chats</button>
    <span class="progress" id="prog"></span>
  </div>
  <input id="q" type="search" placeholder="Search your chats by name&hellip;" autocomplete="off">
  <ol class="chats" id="list">
$ITEMS
  </ol>
  <p class="tip" id="tip"><strong>Your browser stopped some tabs.</strong> That is its pop-up guard,
     not a problem with your chats. Click the remaining chats one by one -
     or allow pop-up windows for this page and press the button again.</p>
  <footer>Made by Z.ai Chat Recovery on $GENERATED. The links open on chat.z.ai and keep working
     even while its chat list looks empty. Keep this file - it is your chat index.</footer>
</div>
<script>
(function () {
  var items = Array.prototype.slice.call(document.querySelectorAll("li.chat"));
  var btn = document.getElementById("next");
  var prog = document.getElementById("prog");
  var tip = document.getElementById("tip");
  var blocked = 0;
  function remaining() {
    return items.filter(function (li) {
      return li.offsetParent !== null && !li.classList.contains("opened");
    });
  }
  function update() {
    var opened = 0;
    items.forEach(function (li) { if (li.classList.contains("opened")) opened++; });
    var left = remaining().length;
    prog.textContent = opened === items.length
      ? "All " + items.length + " chats opened \\u2713"
      : opened + " of " + items.length + " opened \\u00b7 " + left + " to go";
    btn.disabled = remaining().length === 0;
  }
  function mark(li) {
    li.classList.add("opened");
    li.querySelector("a.open").textContent = "Opened \\u2713";
  }
  btn.addEventListener("click", function () {
    var batch = remaining().slice(0, 10);
    for (var i = 0; i < batch.length; i++) {
      var w = window.open(batch[i].querySelector("a.open").href, "_blank");
      if (w) mark(batch[i]);
      else blocked++;
    }
    if (blocked > 0) tip.className = "tip show";
    update();
  });
  items.forEach(function (li) {
    li.querySelector("a.open").addEventListener("click", function () { mark(li); update(); });
  });
  document.getElementById("q").addEventListener("input", function (e) {
    var q = e.target.value.trim().toLowerCase();
    items.forEach(function (li) {
      li.style.display = li.getAttribute("data-title").indexOf(q) !== -1 ? "" : "none";
    });
    update();
  });
  update();
})();
</script>
</body>
</html>
""")


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


# --- leveldb .ldb reading (Snappy blocks) ---------------------------------
# Chrome compacts localStorage records out of the raw-readable .log into
# Snappy-compressed blocks of .ldb table files. A plain byte scan only sees
# the token while it is fresh in the .log; after compaction it goes invisible
# and the app would wrongly report "no sign-in". These helpers read the table
# properly: footer -> index block -> per-block Snappy decompress. Pure stdlib.

def _varint(buf, pos):
    r = 0
    s = 0
    while True:
        b = buf[pos]
        pos += 1
        r |= (b & 0x7F) << s
        s += 7
        if not (b & 0x80):
            return r, pos


def snappy_decompress(data):
    """Raw-format Snappy decompression (the subset LevelDB uses)."""
    pos = 0
    length = 0
    shift = 0
    while True:
        b = data[pos]
        pos += 1
        length |= (b & 0x7F) << shift
        shift += 7
        if not (b & 0x80):
            break
    out = bytearray()
    while pos < len(data) and len(out) < length:
        tag = data[pos]
        t = tag & 3
        if t == 0:  # literal
            n = (tag >> 2) + 1
            if n > 60:  # length follows in 1..4 bytes
                nb = (tag >> 2) - 59
                n = int.from_bytes(data[pos + 1:pos + 1 + nb], "little") + 1
                pos += 1 + nb
            else:
                pos += 1
            out += data[pos:pos + n]
            pos += n
        else:  # copy (back-reference)
            if t == 1:
                ln = ((tag >> 2) & 7) + 4
                off = ((tag >> 5) << 8) | data[pos + 1]
                pos += 2
            elif t == 2:
                ln = (tag >> 2) + 1
                off = int.from_bytes(data[pos + 1:pos + 3], "little")
                pos += 3
            else:
                ln = (tag >> 2) + 1
                off = int.from_bytes(data[pos + 1:pos + 4], "little")
                pos += 4
            if off == 0 or off > len(out):
                raise ValueError("bad snappy back-reference")
            for _ in range(ln):
                out.append(out[-off])
    if len(out) != length:
        raise ValueError("snappy length mismatch")
    return bytes(out)


def ldb_snappy_blocks(path):
    """Yield every Snappy-compressed data block of a .ldb table file."""
    try:
        data = open(path, "rb").read()
        if len(data) < 48 or data[-8:] != struct.pack("<Q", 0xDB4775248B80FB57):
            return
        footer = data[-48:]
        pos = 0
        _, pos = _varint(footer, pos)  # metaindex handle (unused)
        _, pos = _varint(footer, pos)
        ioff, pos = _varint(footer, pos)
        isize, pos = _varint(footer, pos)
        itype = data[ioff + isize]
        idx = snappy_decompress(data[ioff:ioff + isize]) if itype == 1 else data[ioff:ioff + isize]
        n_restarts = int.from_bytes(idx[-4:], "little")
        entries_end = len(idx) - 4 - 4 * n_restarts  # restart array trails entries
        p = 0
        while p < entries_end:
            shared, p = _varint(idx, p)
            non_shared, p = _varint(idx, p)
            val_len, p = _varint(idx, p)
            if shared > 64 or p + non_shared + val_len > entries_end:
                break
            p += non_shared
            handle = idx[p:p + val_len]
            p += val_len
            if not handle:
                continue
            hoff, q = _varint(handle, 0)
            hsize, q = _varint(handle, q)
            if hoff + hsize + 5 > len(data) or data[hoff + hsize] != 1:
                continue  # only Snappy blocks hold surprises; raw scan covers the rest
            try:
                yield snappy_decompress(data[hoff:hoff + hsize])
            except Exception:
                continue
    except Exception:
        return


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
                mtime = os.path.getmtime(f)
                hits = set()
                for blob in (data.decode("ascii", "ignore"),
                             data.decode("utf-16le", "ignore")):
                    hits |= {(m.start(), m.group()) for m in JWT_RE.finditer(blob)}
                # compacted records: Snappy-compressed blocks of the .ldb tables
                if base.endswith(".ldb"):
                    for bi, blk in enumerate(ldb_snappy_blocks(f)):
                        for blob in (blk.decode("ascii", "ignore"),
                                     blk.decode("utf-16le", "ignore")):
                            hits |= {(1_000_000 * (bi + 1) + m.start(), m.group())
                                     for m in JWT_RE.finditer(blob)}
                for pos, tok in hits:
                    email = jwt_email(tok)
                    # z.ai tokens carry an email claim; skip foreign JWTs early
                    if email == "unknown":
                        continue
                    found.append({
                        "mtime": mtime, "pos": pos, "token": tok,
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


def render_reopen_page(chats, outdir, email, msg_counts=None):
    """Write reopen-chats.html - a plain-words, big-type index of every
    recovered chat that reopens it on the website with one click."""
    msg_counts = msg_counts or {}
    esc = lambda s: html.escape(str(s), quote=True)
    items = []
    for c in chats:
        cid = c["id"]
        title = c.get("title") or "(untitled)"
        segs = []
        t = pt_time(c.get("updated_at"))
        if t:
            segs.append("updated " + t)
        if msg_counts.get(cid):
            segs.append(f"{msg_counts[cid]} messages")
        meta = (f'<div class="d">{esc(" · ".join(segs))}</div>' if segs else "")
        items.append(
            '<li class="chat" data-title="%s">\n'
            '  <div class="meta"><div class="t">%s</div>%s</div>\n'
            '  <a class="open" href="https://chat.z.ai/c/%s" target="_blank" rel="noopener">Open &#8594;</a>\n'
            '</li>'
            % (esc(title.lower()), esc(title), meta, cid)
        )
    n = len(chats)
    page = REOPEN_PAGE.substitute(
        COUNTLABEL=f"{n} chat" + ("" if n == 1 else "s"),
        ACCOUNT=esc(email or "your z.ai account"),
        ITEMS="\n".join(items),
        GENERATED=datetime.datetime.now(PT_OFFSET).strftime("%Y-%m-%d %H:%M") + " PT",
    )
    with open(os.path.join(outdir, "reopen-chats.html"), "w", encoding="utf-8") as fh:
        fh.write(page)


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
    ap.add_argument("--open", type=int, default=0, metavar="N",
                    help="after saving, open the N most recent chats in your default browser "
                         "(the browser must be logged in to chat.z.ai)")
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
    msg_counts = {}
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
            msg_counts[cid] = n
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
    render_reopen_page(chats, outdir, email, msg_counts)

    print(f"\nDone. {done} chats recovered, {failed} failed.")
    print(f"Output folder: {os.path.abspath(outdir)}")
    print("Open INDEX.md first. reopen-chats.html is the one-click way back to your")
    print("chats on the website - it works even while the sidebar list is empty.")
    print("Or rerun with --open 10 to reopen the 10 newest chats in your browser.")

    if args.open > 0 and chats:
        n = min(args.open, len(chats))
        print(f"\nOpening the {n} most recent chat(s) in your default browser, one per tab.")
        print(f"Make sure that browser is logged in to chat.z.ai as {email}.")
        for c in chats[:n]:
            print(f"  opening: {(c.get('title') or 'untitled')[:50]}")
            webbrowser.open(f"https://chat.z.ai/c/{c['id']}", new=2)
            time.sleep(0.8)
        print("If a tab shows a sign-in page: log in there, then reopen it from reopen-chats.html.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
