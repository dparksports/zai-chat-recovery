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

**Prerequisite, both ways:** log in to [chat.z.ai](https://chat.z.ai) in your
browser (Google sign-in) at least once on this computer — your conversations
live in that account, and the tool reads that saved sign-in to reach them.
Guest-mode chats exist only inside the browser and cannot be recovered.

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

### 2. Desktop app — Windows, macOS, Linux

Grab the archive for your platform from [Releases](../../releases):

| File | Platform |
|---|---|
| `ZaiChatRecovery-windows-x64.zip` | Windows 10/11 (64-bit) |
| `ZaiChatRecovery-mac-arm64.zip` | macOS 12+ (Apple Silicon) |
| `ZaiChatRecovery-mac-x64.zip` | macOS 12+ (Intel) |
| `ZaiChatRecovery-linux-x64.tar.gz` | Linux x64 (self-contained, includes a `.desktop` file) |

Unzip and run `ZaiChatRecovery` (on macOS: right-click → **Open** the first
time — the build is ad-hoc signed). .NET is bundled, nothing to install.
Two buttons: **Check my chats** — a diagnosis that shows how many
conversations are visible on your account before anything is saved — and
**Save all my chats**. Built-in **"Protect your session IDs"** tips panel.
Scans Chrome, Edge, Brave, Chromium (including snap) *and Firefox* on all
three operating systems. An earlier Windows/WinForms app is superseded by this one
(source kept under `windows-app/` for reference).

![How it works](assets/how-it-works.svg)

## Reading your recovered chats in the app

The desktop app now includes a **reader**: after saving (or at any later
launch - it remembers your last backup and can open any backup folder made
by an earlier version or by the Python CLI). Click **Read my chats** for a
searchable list of every conversation, rendered as chat bubbles with
timestamps, collapsed "assistant thinking" sections, and an
**Open on chat.z.ai** button per chat. You can also start it directly:

```
ZaiChatRecovery --open-reader <backup-folder>
```

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
desktop-app/            # Avalonia app: Windows + macOS + Linux, one codebase
windows-app/            # earlier WinForms app (superseded, kept for reference)
assets/                 # infographics (SVG)
.github/workflows/      # CI: builds win/mac/linux binaries on native runners
LICENSE                 # Apache License 2.0
```

## License

Apache-2.0 — see [LICENSE](LICENSE). Fixes and port reports welcome via
issues/PRs, especially for other browsers and OSes.
