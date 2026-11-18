# AJ139 Battery Tray

A small Windows tray app that shows the battery level and DPI stage of the **AJAZZ AJ139 V2 MC** wireless mouse.

The mouse's own software has no tray mode and no battery percentage, and its USB protocol isn't documented anywhere. I reverse-engineered it by listening to the 2.4G receiver.

<p>
  <img src="docs/panel.png" alt="Details panel" width="320">
</p>

- The tray icon shows the battery %. Its colour shifts from green to yellow to red as the battery drains, with separate shades for light and dark taskbars.
- Click it to open a Windows 11-style panel with the battery, last update, an estimate of time left, and the DPI stages (the active one highlighted).
- It updates live when you press the DPI button on the mouse.
- **Read-only:** it only listens to what the receiver sends and never writes to the mouse.
- **Lightweight:** a single 26 KB exe that sits at ~0% CPU and ~34 MB RAM when idle. The icon only appears while the receiver is plugged in.

## How it works

The receiver (USB `VID 0xA8A5`, `PID 0x2255`) exposes a vendor-defined HID collection on interface `MI_02` (usage page `0xFF01`, 65-byte reports). The mouse sends status messages on it by itself, roughly once a minute and whenever something changes.

| Message | Meaning |
|---|---|
| `AA FA tt xx 38 … D0 pp` | Battery report, sent periodically (`tt` = `00`) and on events (`tt` = `AE`). Byte 10 (`pp`) = battery % |
| `AA FA AE xx 0A … ss` | DPI button pressed. Byte 10 = new stage (1-based) |
| `AA 0E A5 xx … n a [s1 s2 …]` | DPI settings, sent when the vendor app asks: byte 12 = stage count, byte 13 = active stage, then uint16 LE values |
| `AA 03 …` | Firmware strings (`YJX.co.mouse…`, build dates) |

<sub>Byte offsets include the leading report-ID byte.</sub>

I worked out the meaning of each byte by watching how the values changed:
- Battery: byte 10 drops slowly with use, reads the same in both message types, and is exactly 100 after a full charge. Byte 4 looked like a percentage at first, but it differs between the two message types and reached 111, so it measures something else.
- DPI button: the stage number cycles 5 → 6 → 1 → 2 as you press it.
- DPI settings: the uint16 values read as 400 / 900 / 1200 / 1600 / 2400 / 3200, which match the vendor app.

The mouse's battery reading jitters by about ±3%, so the app shows the median of the last 5 readings. Byte 4 of the DPI reply changes between replies (seen `A1` and `90`), so it isn't treated as part of the header.

The app keeps a small log (`log.txt`, next to the exe) of battery readings, the raw status bytes, DPI changes and read errors. It stores the last DPI stages in `dpi.txt` in the same folder, so keep the exe somewhere you can write to.

## Build and run

There's nothing to install. The C# compiler that ships with Windows (.NET Framework 4.x) builds it:

```
build.cmd
AJ139Tray.exe
```

| Option | What it does |
|---|---|
| `--selftest` | Runs the protocol parser against real captured packets (exit code 0 = OK) |
| `--watch [sec]` | Prints live readings to the console |
| `--preview <dir>` | Renders the icon and panel to PNG files |

To start it with Windows, put a shortcut to `AJ139Tray.exe` in `shell:startup` (or create a Task Scheduler task with an "At log on" trigger).

`tools/hidprobe.ps1` is the read-only probe I used to find the protocol. It lists a device's vendor HID channels and logs everything they send:

```
powershell -ExecutionPolicy Bypass -File tools\hidprobe.ps1 -VendorId A8A5 -ProductId 2255 -ListenSec 60
```

## Notes

- I inferred the protocol from observation, so the byte meanings are my best reading and aren't official.
- Only the 2.4G receiver is supported. Over Bluetooth this mouse doesn't expose the standard battery service.
- Not affiliated with AJAZZ.
