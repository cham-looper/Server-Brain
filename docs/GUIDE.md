> This guide is also built into the app: open **Help & guide** in the sidebar (or tray -> Help & guide).

# Server Brain – User Guide

## 1. Where is the dashboard? (web access)

| Address | Works when |
|---|---|
| **http://127.0.0.1:7777** | Always (this is the main address) |
| http://localhost:7777 | Always |
| http://brain.localhost | The "names port" is 80 (default) and free |

- Open it in any browser on **this PC**, or use the app window. Both show the same thing.
- Fastest way: tray icon → right-click → **Open in browser**.
- If port 7777 is taken by another program, Server Brain picks the next free one (7778, 7779…). The real address is always shown in the tray menu → *Open in browser*, and in **Settings → Network**.
- You can change the port in **Settings → Network** (needs a restart of Server Brain).
- The dashboard is **local only**: other PCs and phones on your network cannot open it, and it can never be shared through a public tunnel. This is on purpose, because the dashboard can start programs.

## 2. Addresses of your projects

Each project gets:

| Address | Example | Notes |
|---|---|---|
| **Friendly name** | `http://my-gallery.localhost` | Type it in Chrome / Edge / Firefox. On-demand projects start by themselves. |
| Direct | `http://127.0.0.1:4173` | Always works, also from command-line tools. |
| Public | `https://xxxx.trycloudflare.com` | Only if you switch on public access. |

If port 80 is not available, the friendly name needs the port: `http://my-gallery.localhost:8080` (the dashboard shows the exact link).

`*.localhost` names work in browsers only. `curl`, Node or Python on Windows do not resolve them; use the direct address there.

## 3. First-time setup (5 minutes)

1. **Install** `ServerBrain-Setup-1.0.0.exe`. Keep *Start Server Brain when I sign in* ticked.
2. The window opens. Click **Add project** → **Scan a folder of projects**.
3. Type or pick the parent folder (for example `D:\Local-AI-Studios`) → **Scan this folder**.
4. Review the list:
   - Untick projects you do not want.
   - The dropdown picks the start command (your own `start.bat` is suggested first).
   - Red text = port clash. Click **Fix clashing ports** to move one of them to a free port.
5. Click **Add N projects**. Nothing inside your project folders is changed.
6. On each card choose how it runs (section 4) and press **Start**.

You only do this once. After that you just manage everything from the dashboard.

## 4. Run modes (the segmented switch on every card)

| Mode | Use it for | What happens |
|---|---|---|
| **Manual** | Things you use sometimes | Starts when you press Start. Runs until you press Stop ("once"). |
| **Always** | Things that must always be up | Starts automatically when you sign in to Windows, one after another. Restarted if it crashes. |
| **On demand** | Heavy or rarely used apps (AI tools, GPU) | Uses **no memory or GPU** until you open its address. Starts in a few seconds, stops after N idle minutes (default 15). |

## 5. Everyday use

- **Start / Stop / Restart** buttons on each card. **Start** opens the project in your browser when it is ready (switch off in Settings).
- **▢↗ Open** opens the project; it starts it first if needed.
- **>_ Logs** shows live console output with search, download and clear.
- **⋯ menu**: edit settings, copy address, show folder, public access, remove.
- **Ctrl + K** opens quick actions (start, stop, open, go to a page).
- **Closing the window only hides it.** Server Brain keeps running in the tray (bottom-right, maybe under the ^ arrow). To really exit: tray icon → **Quit Server Brain** (this stops your projects).
- Tray icon: left-click opens the window; right-click lists your projects with Start / Stop / Open.

## 6. Port problems

- **Orange / red box on a card** = two projects share a port, or another program already uses it.
- **Ports** page: everything listening on the PC and which project owns it.
- Fix buttons:
  - **Use a free port** – Server Brain gives the project a new port through its port variable (for example `APP_PORT`). Works only if the project reads its port from the environment. A project that hard-codes the port will ignore it.
  - **Free port (stop …)** – stops the other program holding the port (asks first; system programs are protected).
  - **Start anyway** – tries to start despite the warning.
- A project that cannot start because of a port shows **Port conflict** and tells you who holds the port.

## 7. Public access (share a project on the internet)

1. Open the project → **Public access** tab.
2. Pick **Quick link** (random address, no account) or **My own domain** (needs a Cloudflare tunnel token and hostname).
3. Click **Go public**. Copy the address and the **access code**; visitors must enter the code.
4. **Stop sharing** closes it.

Needs `cloudflared` (already on your PC). Keep the access code on unless the project has its own login.

## 8. Settings

- **Start Server Brain with Windows**, delay between projects, notifications, default idle time.
- Dashboard port, names port, cloudflared path.
- Theme (dark / light / system), accent color, reduce motion, low-power interface.
- **Export / import** your whole setup as a file (backup or move to another PC).

## 9. Troubleshooting

| Problem | Fix |
|---|---|
| Dashboard page does not open | Check the tray icon exists. Tray → **Open in browser**. If no tray icon, start *Server Brain* from the Start menu. |
| `name.localhost` does not open | Use Chrome/Edge/Firefox. If it still fails, port 80 is probably used by another program: use the link shown on the card (it includes `:8080`), or the direct `127.0.0.1:PORT`. |
| Project shows *Starting…* for long | First runs may install packages. Open **Logs**. If the log shows another port, click **Use port N** in the yellow box. |
| Project keeps restarting | Open **Logs** for the error. After the maximum restarts it shows **Failed** and stops trying. |
| A browser tab opens by itself on each start | Your own `start.bat` does that. Choose a direct command (e.g. `npm start`) in the project's Settings. |
| "Windows protected your PC" when installing | The installer is not code-signed. Choose *More info → Run anyway*. |
| Something crashed | See `%LOCALAPPDATA%\ServerBrain\crash.log` and send it along. |

## 10. Where things live

| What | Where |
|---|---|
| Program | `C:\Program Files\Server Brain` (or `%LOCALAPPDATA%\Programs\Server Brain` for a per-user install) |
| Settings, project list, logs | `%LOCALAPPDATA%\ServerBrain` |
| Your projects | Wherever they already are. Server Brain never writes into them. |

## 11. Uninstall

Windows **Settings → Apps → Server Brain → Uninstall**. It stops the app, removes the startup entry and asks whether to also delete your settings and project list. Your project folders are never touched.
