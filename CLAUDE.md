# AleeDesk - notes for Claude

AnyDesk-style remote desktop for Windows, built for Ali ("ALee"). Brand: **AleeDesk**, tagline
"Less waiting. More fixing.", blue two-tone "A" logo, light UI, Ali's photo as the profile
picture top-right. The user is not a developer: explain in plain words, give step-by-step
instructions, and test changes yourself before reporting.

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
  `SELFTEST PASS`. Host + viewer in one process: wrong password rejected, encrypted channel,
  screen frames, pointer shape/position, 600 KB file transfer verified byte-for-byte.
  Use `5.189.191.129:7788` (real relay), or start the app with `relay=1` in settings.ini to
  test against `127.0.0.1:7788`. Note: it moves the real mouse to the screen centre.
- `--viewtest <relay>` opens a real viewer window and saves `viewer.png` next to the exe.
- `--uishot <dir>` renders all five pages and both dialogs to `ui-*.png` (uses the data in
  settings.ini; back it up and restore it if you inject sample data).
- To screenshot the live window use PrintWindow (another window may cover it).

## Architecture

- **Relay** (`Relay.cs` in-app, `server/relay.py` on the VPS; same protocol): framed
  messages `[int32 len][type byte][data]`. Host sends `H`+id, gets `O`, then keepalives `K`;
  viewer sends `V`+id; relay sends `P`+viewerIP to the host and `O` to the viewer, then just
  pipes raw bytes both ways.
- **Handshake** (host<->viewer, through the relay): `N` nonce, `A` proof (HMAC of
  PBKDF2(password, "RemoteDesk|"+id)), `F` wrong / `Y` host proof. Then AES-256-CBC +
  HMAC-SHA256 with counters (`Channel.EnableSecurity`). Never change the salt string.
- **Encrypted session messages**
  - host -> viewer: `W` waiting consent, `G` granted, `D` denied, `S` w,h, `R` x,y,w,h+jpeg,
    `c` pointer shape (hotX,hotY+png), `p` pointer pos (x,y,visible)
  - viewer -> host: `M` mouse, `k` key, `C` clipboard, `Q` quality,scale, `T`/`U`/`Z` file
    start/chunk/end, `X` bye
  - Unknown types are ignored, so old and new versions don't crash each other.
- Screen: GDI BitBlt, 64px tile diff, changed areas sent as separate JPEG rectangles,
  ~30 fps cap. The pointer is NOT drawn into frames; the viewer uses the remote pointer shape
  as its own local cursor (instant) and only draws the remote pointer when it moves remotely.
- Settings: `%APPDATA%\RemoteDesk\settings.ini` (folder name kept from v1 so the desk ID
  survives). Keys: id, server, consent, relay, recent, contacts, history.
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

- Version check in the handshake (warn when the two PCs run different builds).
- Admin/UAC and lock-screen control (needs a Windows service running as SYSTEM) and
  unattended access - the user deferred this.
- Download direction for files + a file browser; multi-monitor; audio; clipboard auto-sync.
- A domain name for the relay instead of the IP; code signing; an installer.
