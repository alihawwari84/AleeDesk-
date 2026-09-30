# AleeDesk - notes for Claude

AnyDesk-style remote desktop for Windows, built for Ali ("ALee"). Brand: **AleeDesk**, tagline
"Less waiting. More fixing.", blue two-tone "A" logo, light UI, Ali's photo as the profile
picture top-right. The user is not a developer: explain in plain words, give step-by-step
instructions, and test changes yourself before reporting.

## Working routine (the user asked for this)

The user works on this project from two PCs, with Claude on both. GitHub keeps them in sync.
- **Start of a session:** `git pull` before changing anything.
- **When the user says "finish for today"** (or "done for today" etc.): run `git status`,
  check nothing unwanted is staged (no `dist/`, no secrets), commit everything with a clear
  message, and `git push`. Then tell the user in plain words that the work is saved.
- If no GitHub remote is set yet, say so and ask for the repository address.

## Build and run

- `powershell -ExecutionPolicy Bypass -File build.ps1` -> `dist\ALeeDesk.exe` (single exe).
- Compiler is `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe` = **C# 5 only**:
  no `$"..."`, `?.`, `=>` members, `nameof`, `out var`, tuples. Lambdas/`??` are fine.
- No dotnet SDK, no NuGet, no Python on the dev PC. WinForms + System.Drawing only.
- Adding a source file: also add it to the list in `build.ps1`.
- `assets/app.ico` (A mark) is the exe icon; `assets/avatar.png` is embedded as
  `ALeeDesk.avatar.png`. The icon was generated with the same geometry as `Theme.DrawMark`.

## Tests (run these after changes)

- `dist\ALeeDesk.exe --selftest <relay>` -> writes `dist\selftest.txt`, ends with
  `SELFTEST PASS` (takes 1-2 minutes). Host + viewer in one process: wrong password rejected,
  encrypted channel, screen frames, pointer, drag-drop file transfer, clipboard both ways,
  file manager (list, upload into folder, folder download), LAN discovery + direct session,
  unattended vs. attended access, remote restart + reconnect (restart is a stand-in).
  Use `5.189.191.129:7788` (real relay). Close the app first (it holds UDP 7789/TCP 7790).
  Note: it moves the real mouse to the screen centre.
- `--viewtest <relay>` opens a real viewer window and saves `viewer.png` next to the exe.
- `--uishot <dir>` renders the five pages, the file manager and the dialogs to `ui-*.png`
  (uses the data in settings.ini; back it up and restore it if you inject sample data).
  The self-test doesn't open windows, so check new UI with `--uishot` - it caught a crash
  in the file manager that the self-test couldn't.
- To screenshot the live window use PrintWindow (another window may cover it).

## Architecture

- **Relay** (`Relay.cs` in-app, `server/relay.py` on the VPS; same protocol): framed
  messages `[int32 len][type byte][data]`. Host sends `H`+id, gets `O`, then keepalives `K`;
  viewer sends `V`+id; relay sends `P`+viewerIP to the host and `O` to the viewer, then just
  pipes raw bytes both ways.
- **Handshake** (host<->viewer, through the relay): `N` nonce, `A` proof (HMAC of
  PBKDF2(password, "RemoteDesk|"+id)), `F` wrong / `Y` host proof. Then AES-256-CBC +
  HMAC-SHA256 with counters (`Channel.EnableSecurity`). Never change the salt string.
- **Passwords:** the one-time session password needs someone to click Accept; the permanent
  unattended password (Settings) skips that. Both are case-insensitive (upper-cased before
  PBKDF2). Only the derived key is stored, DPAPI-encrypted (`Secret` class). 10 wrong
  passwords in 10 minutes locks everyone out for a while.
- **Encrypted session messages**
  - host -> viewer: `W` waiting consent, `G` granted, `D` denied/busy, `S` w,h,
    `R` x,y,w,h+jpeg, `c` pointer shape (hotX,hotY+png), `p` pointer pos (x,y,visible),
    `C` clipboard text, `q` restarting (1 = will come back), file manager replies:
    `l` listing, `f`/`d`/`e` file start/chunk/end, `n` request done, `x` error, `a` upload saved
  - viewer -> host: `M` mouse, `k` key, `C` clipboard, `Q` quality,scale, `T`/`U`/`Z` file
    to Desktop, `j`/`U`/`Z` file into a folder, `L` list folder, `g` get file/folder,
    `r` restart, `X` bye
  - Unknown types are ignored, so old and new versions don't crash each other.
- **LAN** (`Lan.cs`): UDP 7789 announcements/queries (`ALDA|id|name|port|version`, `ALDQ`),
  TCP 7790 direct sessions (same opening as the relay). Viewer tries direct first, then relay.
- **Tray / autostart:** HKCU Run `AleeDesk` = `"exe" --background`; closing the window hides
  it while unattended/autostart is on; launching the exe again signals the running copy
  (named event `RemoteDesk.Show`) to show its window.
- **Restart:** host runs `shutdown /r /t 5`; the viewer's main window reconnects for up to
  5 minutes. Only works with the unattended password + autostart, and only after Windows
  signs in (no Windows service yet).
- Screen: GDI BitBlt, 64px tile diff, changed areas sent as separate JPEG rectangles,
  ~30 fps cap. The pointer is NOT drawn into frames; the viewer uses the remote pointer shape
  as its own local cursor (instant) and only draws the remote pointer when it moves remotely.
- Settings: `%APPDATA%\RemoteDesk\settings.ini` (folder name kept from v1 so the desk ID
  survives). Keys: id, server, consent, relay, lan, unattended, ukey (encrypted), saved
  (encrypted remembered passwords per desk), recent, contacts, history.
- One instance per PC (mutex `RemoteDesk.SingleInstance`) because the ID is per PC.

## Relay server (VPS)

- Contabo Cloud VPS 4, Ubuntu 24.04, `5.189.191.129`, hostname vmi3619637. The user logs in
  with `ssh root@5.189.191.129` (never ask for or type the password yourself).
- `/root/relay.py` run by systemd unit `remotedesk-relay` (copies in `server/`).
- `ufw` is active on the server (someone added SMB rules); it must keep `7788/tcp` allowed.
  If port 7788 times out while SSH (22) works, check `ufw status` first.
- Default relay is built in: `Brand.DefaultRelay` in `MainForm.cs`. Old installs with
  `127.0.0.1:7788` migrate to it unless they run their own relay.

## Environment gotchas (Claude Code on Windows)

- Very long PowerShell commands fail (ENAMETOOLONG): write big files with the Write tool.
- `Remove-Item` may be blocked by the sandbox: use `[IO.File]::Delete(path)`.
- `Add-Type` types don't persist between PowerShell calls; redefine them in the same call.

## Ideas / not done yet

The user asked for AnyDesk's feature list. Done: LAN discovery, unattended access, remote
restart + reconnect, file manager + clipboard sync. Still to do, roughly in order:
- **Adaptive streaming** (quality/frame rate follow the connection); later real H.264 via
  Windows Media Foundation instead of JPEG tiles.
- **Teams "Request support"** button posting to an IT channel via a Workflows webhook, with
  `aleedesk://connect/<id>` links (the user picked this option).
- Windows service (SYSTEM) for UAC prompts, lock screen and reconnect before sign-in.
- Browser viewer for Mac/Linux/phones (native clients are separate projects).
- Version check in the handshake; multi-monitor; audio; domain name for the relay; code
  signing; installer.
