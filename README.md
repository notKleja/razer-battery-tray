# Razer Battery Tray

A tiny Windows 11 tray app that shows your Razer wireless mouse's battery level as a number, styled exactly like the taskbar's own `ENG` input indicator. Hover over it for the device name, percentage and charging state.

- No Razer Synapse, Electron or drivers needed. It talks to the mouse directly over HID.
- Starts with Windows automatically.
- Tested on a **Razer Viper V3 Pro** (HyperSpeed dongle). Other Razer mice using the same protocol should work.

## Install

Download an exe from the [latest release](https://github.com/notKleja/razer-battery-tray/releases/latest):

- **`RazerBatteryTray-win-x64-standalone.exe`** (48 MB) works out of the box.
- **`RazerBatteryTray-win-x64.exe`** (0.2 MB) needs the [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0).

Put it somewhere permanent, such as `%LOCALAPPDATA%\Programs\RazerBatteryTray`, and run it. The exe isn't code-signed, so SmartScreen may warn on first run. Choose **More info → Run anyway**.

### Build from source

Requires the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0). In PowerShell:

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o "$env:LOCALAPPDATA\Programs\RazerBatteryTray"
```

Run `RazerBatteryTray.exe` from that folder. On first launch it adds itself to `HKCU\...\Run` so it starts at login. If you remove that entry or disable it in Task Manager, it stays off. If the icon lands in the hidden-icons area behind `^`, drag it onto the taskbar.

Right-click the icon for **Refresh now** and **Exit**. To remove the app completely, exit it, delete the folder, and delete the `RazerBatteryTray` value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` and the `HKCU\Software\RazerBatteryTray` key.

## Troubleshooting

If it shows `–` or the wrong value, run:

```powershell
& "$env:LOCALAPPDATA\Programs\RazerBatteryTray\RazerBatteryTray.exe" --probe
```

This writes `RazerBatteryTray-probe.txt` to your temp folder and opens it. The file lists every Razer HID interface the app found and how each one answered. Attach it when you report an issue. Razer Synapse can hold the device and make reads fail, so try again with Synapse closed.

## How the battery is read

Razer mice use a 90-byte HID feature report (report ID 0):

| Byte | Meaning |
|---|---|
| 0 | status (`0x02` ok, `0x01` busy, `0x04` timeout = mouse asleep) |
| 1 | transaction id (`0x1f` modern mice, `0x3f` older ones, `0xff` some dongles) |
| 5 | data size (`0x02`) |
| 6 / 7 | command class / id: `0x07`/`0x80` battery, `0x07`/`0x84` charging |
| 8–87 | arguments |
| 88 | XOR of bytes 2–87 |

Battery percent = `args[1] * 100 / 255`, and charging when `args[1] == 1`. The app opens the HID collection with zero access rights, which Windows allows for feature reports even on mouse collections. It then tries each transaction id and remembers the one that answers. Interfaces whose top-level collection is a mouse are used first, and when one exists no other Razer device is queried, so a Razer headset or keyboard can't be reported in place of the mouse.

Protocol details come from these projects:
- [OpenMouse-Project/mouse-protocol](https://github.com/OpenMouse-Project/mouse-protocol)
- [OpenMouse-Project/openmouse](https://github.com/OpenMouse-Project/openmouse)
- [Tekk-Know/RazerBatteryTaskbar](https://github.com/Tekk-Know/RazerBatteryTaskbar)

## How the text matches the taskbar

The number is drawn in Segoe UI Variable Small at 12 epx, the same as `ENG`, with the same vertical position and the taskbar's text color for light and dark themes. The taskbar renders text with DirectWrite, which boosts the contrast of anti-aliased edges. The app applies `coverage^0.4`, which was fitted against the real `ENG` pixels, so the stroke weight matches.

App tray icons are a fixed 16×16 epx square, so a `%` sign doesn't fit at this size. The full percentage is in the tooltip instead.

## License

MIT
