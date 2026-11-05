# Server Brain – plan, decisions and roadmap

## Goal
One Windows app that runs all local projects (Node.js, Python, PHP, Docker, anything with a command) automatically at login,
keeps them alive, flags port problems, opens them by name in the browser, and can share them publicly — while using almost no
hardware when idle. The same dashboard also runs in a web browser. Windows first; Linux optional.

## Decisions (and why)

| Question | Decision | Reason |
|---|---|---|
| Runtime / language | **.NET 9** engine + WinForms tray shell | ~25 MB idle; native Job Objects & TCP-table APIs; installs with no runtime for the user; same engine runs on Linux. Electron was rejected (150 MB+ just for the manager). |
| Window | **WebView2** hosting the web dashboard, created on open and **disposed on close** | One UI codebase for "app" and "web"; no web-view RAM while hidden. |
| UI | React + Vite + Framer Motion, embedded in the engine assembly | Rich animation; ships inside the exe; no external files. |
| Start with Windows | **At user login** (per-user `Run` key, hidden in tray) | No password needed; projects see the user's own PATH, PHP, caches and GPU session. A boot-time Windows Service was deliberately *not* done: a service runs as another account, can't see per-user PATH/tools/caches (breaks `nvm`, user-installed PHP, HuggingFace caches, GPU apps) and needs a stored password. Revisit if the PC is headless without auto-login (see roadmap). |
| Process control | **Job Object per project** + kill-on-close | Stopping kills the whole tree (npm → node → vite…), even orphans; no leftovers if the manager dies. |
| Friendly URLs | Built-in reverse proxy for `*.localhost` (YARP forwarder) | Works with zero setup in Chrome/Edge/Firefox; enables on-demand start, idle shutdown and safe public sharing through one gate. |
| Public URLs | **Cloudflare Tunnel** (cloudflared already installed): quick link or own domain via token | No open router ports; per-project; HTTPS for free. |
| Port conflicts | Checked at add time, after every change, and **before every start**; block + explain + offer fixes | Matches the requirement "flag me, proper error handling". |
| Installer | **Inno Setup** exe, per-user (no UAC), uninstaller, MIT license page | Small, standard, silent-install capable. |

## Features delivered
Run modes (Manual / Always / On-demand), restart policy with back-off, dependency order, staggered boot, CPU priority & memory limit,
stop command, live logs (ring buffer + rotating files), CPU/RAM metrics, port scanner & conflict engine, free-port finder,
safe "free this port" action, bulk folder scan with read-only detection, import/export, activity timeline, notifications,
command palette, dark/light themes + accents + low-power UI, tunnels with access-code gate, DPAPI secrets, single instance,
crash log, clean logoff shutdown, installer/uninstaller, headless Linux daemon.

## Things the user didn't list but are included
Port-clash auto-fix in bulk import, dependency ordering, memory limit, "start anyway", read-only guarantee for project folders,
access-code protection for public links, header rewriting behind the friendly name, notifications, backup/restore,
unit tests, security hardening of the local API (Host/Origin checks).

## Known limits (honest list)
- **Hard-coded ports**: if an app ignores environment variables, "move to a free port" cannot change what it listens on. The UI says so.
- **Start scripts that open a browser** do so at every start (we can't edit them). The wizard flags this and suggests a direct command.
- `*.localhost` names work in browsers; command-line tools on Windows (curl, Node, Python) don't resolve them — use the direct `127.0.0.1:port`.
- Stop = kill (like closing a terminal). Use a *stop command* for apps that need a graceful shutdown.
- Docker containers are managed through their CLI command; stop command `docker compose down` is suggested automatically.
- Auto-update is not built in (the installer upgrades in place).

## Roadmap ideas
1. Optional **boot service mode** (Task Scheduler "At startup" running as the user, password stored in Credential Manager) for headless PCs.
2. Health checks (HTTP path) with auto-restart on failing probe.
3. Per-project log retention settings and structured log search.
4. Project templates / "clone and run" from a git URL.
5. Signed installer + winget manifest + auto-update channel.
6. macOS shell (tray via the same engine).
