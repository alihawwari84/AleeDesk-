# AleeDesk

AnyDesk-style remote desktop for Windows. *Less waiting. More fixing.*

- Connect to another PC with its 9-digit desk ID and password
- Live screen with mouse and keyboard control, instant local pointer
- File transfer (drag a file onto the remote screen), clipboard text
- Devices, contacts and session history
- End-to-end encrypted; the relay server only pairs the two PCs and forwards encrypted bytes

## Build

Needs only Windows (uses the C# compiler that ships with .NET Framework 4):

```
powershell -ExecutionPolicy Bypass -File build.ps1
```

Output: `dist\ALeeDesk.exe` - a single file, no installer. Copy it to any PC and run it.

## Relay server

The app uses the built-in relay `5.189.191.129:7788` by default (see `Brand.DefaultRelay`
in `src/MainForm.cs`). The server is a small Python script:

- `server/relay.py` - the relay
- `server/remotedesk-relay.service` - systemd unit that keeps it running

Set up on Ubuntu (as root):

```
cp relay.py /root/relay.py
cp remotedesk-relay.service /etc/systemd/system/
systemctl daemon-reload
systemctl enable --now remotedesk-relay
ufw allow 7788/tcp
```

Also allow TCP 7788 in the hosting provider's firewall if it has one.

A PC can also act as the relay itself: Settings -> "Run the relay server on this PC".

## Layout

| Path | What |
|---|---|
| `src/Protocol.cs` | framing, key derivation, encrypted channel |
| `src/Relay.cs` | relay built into the app |
| `src/Host.cs` | screen capture, pointer, input injection, incoming files |
| `src/Viewer.cs` | connecting, remote screen window, sending files |
| `src/MainForm.cs` | main window, pages, settings storage |
| `src/Ui.cs` | theme, custom controls, dialogs |
| `src/Native.cs` | Win32 calls |
| `src/Program.cs` | entry point and test modes |
| `assets/` | icon, logo mark, profile photo |
| `server/` | relay server for Linux |
