# PeriphHub

One Windows 10/11 app for all your peripherals — Logitech mice, keyboards and
headsets, ASUS laptop keyboard lighting and performance modes, and any other
Bluetooth/USB mouse, keyboard or headset — instead of Logitech Options+,
G HUB, Armoury Crate and MSI Center.

- **Installer, not a portable app**: `PeriphHub-Setup-<version>.exe` installs to
  `C:\Program Files\PeriphHub`, adds Start-menu entries, an uninstaller
  (Settings › Apps), and optionally starts with Windows in the tray.
- **No setup per device**: devices are detected automatically when you plug
  them in or connect them over Bluetooth. Settings are stored per *physical*
  device (Logitech devices by their unit ID), so moving the MX Master from the
  USB receiver to Bluetooth and back keeps every setting and re-applies it
  instantly — also after sleep, reconnect or reboot.
- **Share it**: friends just run the same setup. Their devices are discovered
  the same way; nothing is hard-coded to one PC. Settings › Export lets you
  hand over your whole configuration.
- No .NET install needed — the runtime is bundled.

> Close Logitech Options+ / G HUB / Armoury Crate while using PeriphHub; they
> overwrite the same device settings.

## What works

### Logitech (HID++ 2.0 — Unifying, Bolt, LIGHTSPEED, Bluetooth, cable)

The device page is built from the features the device itself reports, so
other Logitech models light up automatically.

| Feature | Notes |
| --- | --- |
| Battery level / charging | Battery status, unified battery, voltage and ADC (headsets) |
| DPI | Slider over the exact range the sensor supports; DPI cycle and DPI-shift (sniper) buttons |
| Polling rate, onboard / software mode | G-series mice |
| SmartShift | Ratchet / free-spin / SmartShift with sensitivity |
| Hi-res scrolling, natural scrolling | `0x2121` |
| Thumb wheel | Horizontal scroll (custom speed), volume, zoom, tabs, desktops, Alt-Tab, invert |
| Button remapping | Every divertable button: keystrokes, text, programs/URLs, media, mouse, Windows and browser actions |
| Gestures | Gesture button: click / up / down / left / right, each assignable |
| Per-application profiles | Different button jobs in e.g. `chrome.exe`, `photoshop.exe` |
| Easy-Switch | See / switch the host channel |
| Fn-key inversion | Logitech keyboards |
| Headset sidetone, equalizer + presets | `0x8300`, `0x8310` |
| RGB lighting | Static / breathing / cycle / off (`0x8070`) |

Your devices: **MX Master 2S** (receiver or Bluetooth) is fully covered by the
table above. **G321 LIGHTSPEED** is driven over its USB dongle; whatever it
reports (battery, sidetone, EQ, …) appears automatically. Logitech headsets
do not expose settings over Bluetooth — on Bluetooth you get the battery level
Windows reports.

### ASUS laptops (TUF / ROG / Vivobook) — via the ASUS System Control Interface driver

- Model number shown automatically (read from the BIOS)
- Keyboard RGB: static, breathing, colour cycle, strobing; colour picker; speed
- Backlight brightness (off / low / medium / high)
- Lighting while awake / boot / sleep / shutdown (TUF)
- Operating mode Silent / Performance / Turbo (same as Fn+F5)
- Battery charge limit (40–100 %)
- Panel overdrive, GPU Eco mode (when supported)
- Live fan speeds and temperatures

Per-key-RGB ROG models over USB (Aura HID) are supported for effects and
brightness.

### MSI and other keyboards / mice / headsets

- Detected over USB and Bluetooth, battery level over Bluetooth
- **Key remapping & macros** for *every* keyboard (laptop, MSI, anything):
  click *Add key*, press the key, choose what it does — keystrokes, text
  macros, launch apps/sites, media keys — with per-application profiles.

## Honest limits

- **MSI lighting**: MSI has not published the protocol for its peripherals'
  RGB. PeriphHub detects the keyboard and shows its hardware IDs under
  *Diagnostics*; lighting needs one USB capture from MSI Center to add.
- **Logitech Flow** (cursor across computers), **firmware updates** and
  **G-series onboard macro editing** are not implemented.
- Key remapping applies to all keyboards at once (Windows cannot tell which
  keyboard a hooked key came from) and does not affect apps running as
  administrator unless PeriphHub is started as administrator too.
- The installer is not code-signed yet, so Windows SmartScreen shows
  “Windows protected your PC” — click **More info › Run anyway**.

## Finding your laptop model

PeriphHub shows it on the laptop's page. Without the app: **Settings › System
› About** (“Device name”/“Model”), or run `wmic csproduct get name` in a
terminal, or check the sticker under the laptop.

## Building

Needs the .NET 10 SDK and NSIS.

```bash
./build.sh          # Linux / CI  → out/installer/PeriphHub-Setup-<version>.exe
```

```powershell
./build.ps1         # Windows
```

Every push touching `periphhub/` builds the installer in GitHub Actions (artifact
`PeriphHub-Setup`); pushing a tag `periphhub-v<version>` publishes it as a
release.

## Layout

```text
src/PeriphHub/
  Core/        settings store, capability model, actions, key hook, hot-plug hub
  Drivers/
    Logitech/  HID++ 2.0 transport, feature discovery, device page
    Asus/      ATKACPI (Armoury Crate interface) + USB Aura
    Generic/   Bluetooth battery, MSI / other vendors
  UI/          tray icon, converters, dialogs
installer/     NSIS script
```

Logs: `%APPDATA%\PeriphHub\periphhub.log` · Settings: `%APPDATA%\PeriphHub\settings.json`
