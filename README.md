# Z.ai Chat Recovery

![Z.ai Chat Recovery](assets/banner.svg)

[![License: Apache-2.0](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](LICENSE)
[![Python 3.8+](https://img.shields.io/badge/python-3.8%2B-3776ab.svg)](recover_zai_chats.py)
[![Windows app](https://img.shields.io/badge/Windows-app-0078d4.svg)](windows-app/)

Your **chat.z.ai sidebar suddenly shows nothing** — every conversation appears
gone. Before you panic: an empty sidebar is almost always a **frontend
rendering bug**, not lost data. The conversations stay on Z.ai's servers, and
each one still opens by its direct URL (`chat.z.ai/c/<session-id>`). This tool
recovers **all of them** into local files, read-only, in about a minute.

Built and battle-tested during the October 16, 2026 incident (empty sidebar
after the `zchat.z.ai` rollout): 23/23 conversations recovered, transcripts,
JSON, and direct URLs included.

## Two ways to run it

### 1. Python script (Windows / macOS / Linux)

Requires Python 3.8+ and any Chromium browser (Chrome / Edge / Brave /
Chromium) on the machine where you're logged in to chat.z.ai. No packages,
no install:

```
python recover_zai_chats.py
```

That one command finds your session token in browser local storage, verifies
it, downloads every conversation, and writes a timestamped folder:

| File | What it is |
|---|---|
| `INDEX.md` | all chats with links and message counts |
| `chat-urls.md` | direct `chat.z.ai/c/<id>` URLs — they open in your browser even while the sidebar is broken |
| `<title>__<id>.md` | one readable transcript per chat |
| `<id>.json` | raw API JSON (lossless copy) |

Options: `--out DIR` (output folder), `--limit N` (quick test),
`--token eyJ…` (manual token — F12 → Application → Local Storage →
`https://chat.z.ai` → key `token`).

### 2. Windows app (no Python needed)

Download **`ZaiChatRecovery-windows-x64.zip`** from the
[Releases](../../releases) page, unzip, run `ZaiChatRecovery.exe`
(.NET 10 self-contained — nothing else to install). Same engine, plus a
point-and-click flow and a built-in **"Protect your session IDs"** panel.

![How it works](assets/how-it-works.svg)

## Keep your sessions recoverable

![Do and don't](assets/do-dont-tips.svg)

The one-line version: **the session ID is the key.** Chats stay reachable at
`chat.z.ai/c/<session-id>` for as long as the account and its session exist —
so save the URLs and back up before you log out, clear anything, or run a
"cleanup" tool.

## Troubleshooting

| Symptom | Meaning / fix |
|---|---|
| "Only guest/invalid tokens found" | Your browser is in a guest session. Log in to chat.z.ai (not as guest), then rerun. |
| Token rejected (401/403) | Session expired. Log in to chat.z.ai in the browser, then rerun. |
| "API returned an EMPTY list" | You may be signed into a different account than the one holding the chats (e.g. Google vs email sign-in create separate accounts). Log in with that account and rerun. |
| No browsers found | The script checks default profile locations for Chrome/Edge/Brave/Chromium. Use `--token`. |

## Safety

- **Read-only**: only `GET /api/v1/chats`, `GET /api/v1/chats/{id}` and
  `POST /api/v1/chats/{id}/messages/batch` (a content fetch). The tool cannot
  delete, edit, or send anything to your chats.
- **No exfiltration**: requests go to `chat.z.ai` only; your token stays on
  this machine. No dependencies, no telemetry.
- **Use only on your own account.**

## Repository layout

```
recover_zai_chats.py    # single-file cross-platform CLI (stdlib only)
windows-app/            # C# WinForms app (same engine + tips panel)
assets/                 # infographics (SVG)
LICENSE                 # Apache License 2.0
```

## License

Apache-2.0 — see [LICENSE](LICENSE). Fixes and port reports welcome via
issues/PRs, especially for other browsers and OSes.
