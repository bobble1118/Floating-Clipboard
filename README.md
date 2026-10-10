<p align="center"><img src="docs/icon.png" width="96" alt="Floating Clipboard icon"></p>

<h1 align="center">Floating Clipboard</h1>

<p align="center">A PastePal-style clipboard manager and drag-and-drop file shelf for Windows, summoned by holding <kbd>Alt</kbd>.</p>

## Features

- **Hold <kbd>Alt</kbd>** to pop up a floating, always-on-top acrylic glass panel next to your cursor. Hold again to hide it.
  - Like <kbd>Win</kbd>+<kbd>V</kbd>, the panel never takes focus: your caret stays where it was, so clicking an item pastes right there. The panel stays open, so you can paste several items in a row.
- **File shelf**: drag files or folders onto the panel to park them, then drag them out into any other app (Explorer, chat apps, editors, browsers…).
  - The panel starts as a small square and grows with the number of files, up to 3 × 4 items (a single item sits centered); beyond that it scrolls instead of covering the screen.
  - Hover an item and click **✕** to remove it from the shelf. This never deletes the file itself.
  - Double-click an item to open it.
- **Clipboard history**, hidden by default. **Tap <kbd>Alt</kbd>** while the panel is open to expand it.
  - Records text, images (stored as PNG) and copied files, newest first; copying something again moves it back to the top.
  - Click the search box to type (only then does the panel take focus), then click an item (or press <kbd>Enter</kbd>) to paste it into the window you were using before.
  - History items can also be dragged out.
- **Privacy**: anything marked by password managers as "do not record" (`ExcludeClipboardContentFromMonitorProcessing`) is skipped.
- **Tray icon** with settings, and **starts with Windows** by default.

## Download

Grab the latest build from [Releases](https://github.com/bobble1118/Floating-Clipboard/releases/latest):

| File | Size | Needs |
|---|---|---|
| `FloatingClipboard-Setup.exe` (recommended) | ~47 MB | Nothing: installs per user, adds a Start menu shortcut, shows a how-to page |
| `FloatingClipboard-win-x64.exe` | ~68 MB | Nothing, .NET is bundled |
| `FloatingClipboard-win-x64-fd.exe` | ~0.2 MB | [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |

If you use a bare exe instead of the installer, put it somewhere permanent (e.g. `%LOCALAPPDATA%\Programs\FloatingClipboard`) before running it, since "Start with Windows" points at wherever the exe lives. The exe isn't code-signed, so Windows SmartScreen may warn on first launch: choose **More info → Run anyway**.

## Usage

| Action | What it does |
|---|---|
| Hold <kbd>Alt</kbd> (~0.45 s, adjustable) | Show / hide the panel |
| Tap <kbd>Alt</kbd> while the panel is open | Expand / collapse clipboard history |
| Drag files onto the shelf | Keep them for later |
| Drag an item out | Copy it into another app |
| Hover + **✕** or <kbd>Del</kbd> | Remove the item |
| <kbd>Ctrl</kbd>+<kbd>Del</kbd> while the panel is open | Clear the whole shelf (files themselves are untouched) |
| Click / <kbd>↓</kbd> + <kbd>Enter</kbd> | Paste a history item (<kbd>Ctrl</kbd>/<kbd>Shift</kbd>+click to multi-select) |
| <kbd>Ctrl</kbd>+<kbd>P</kbd> | Pin / unpin the panel at its current position (accent border = pinned) |
| <kbd>Esc</kbd> | Hide the panel |
| Drag the panel's edge | Move it |

Holding <kbd>Alt</kbd> together with any other key (<kbd>Alt</kbd>+<kbd>Tab</kbd>, <kbd>Alt</kbd>+<kbd>F4</kbd>…) never triggers the panel.

### Tray settings

Right-click the tray icon (bottom-right of the taskbar):

- **Start with Windows**: on by default (`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`)
- **Default position**: at the cursor, or the top-left corner of the screen (a pinned position takes priority)
- **Alt hold time**: 0.3 / 0.45 / 0.6 / 0.8 s
- **History limit**: 50 / 100 / 300 / 1000 items
- **Clear history** / **Clear shelf** / **Quit**

Left-click the tray icon to show or hide the panel.

## Requirements

- Windows 10 or 11 (64-bit)
- The .NET 8 SDK to build from source

For the frosted-glass look, turn on **Settings → Personalization → Colors → Transparency effects**. With it off, the panel falls back to a translucent dark background.

## Build & run

```powershell
git clone https://github.com/bobble1118/Floating-Clipboard.git
cd Floating-Clipboard
dotnet build -c Release
.\bin\Release\net8.0-windows\FloatingClipboard.exe
```

### Installer

The installer (`installer/FloatingClipboard.iss`, built with [Inno Setup 6](https://jrsoftware.org/isinfo.php)) installs per user without admin rights, adds a Start menu shortcut, and shows a page of shortcuts and basic usage (`installer/howto.txt`) at the end. Uninstalling also removes the "Start with Windows" entry.

```powershell
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o bin\publish
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer\FloatingClipboard.iss   # -> bin\installer\FloatingClipboard-Setup.exe
```

The app lives in the tray and never shows a taskbar button. Only one instance can run at a time.

Command-line flags:

- `--show`: open the panel right after launch
- `--selftest`: run the Alt-detection self-check and exit (exit code 0 means it passed)

## Data

Everything is stored locally in `%APPDATA%\FloatingClipboard`:

| File | Contents |
|---|---|
| `store.json` | Clipboard history and shelf |
| `images\` | Clipboard images as PNG |
| `settings.json` | Tray settings |

History is stored as plain text, so use **Clear history** if you copied something sensitive that wasn't marked private. The shelf only remembers file *paths*; the files themselves stay where they are.

## Notes

- Hotkeys are detected with a low-level keyboard hook. Windows doesn't deliver keys from elevated (administrator) windows to a non-elevated app, so holding <kbd>Alt</kbd> over an admin window does nothing unless Floating Clipboard is also run as administrator.
- Paste works by restoring focus to your previous window and sending <kbd>Ctrl</kbd>+<kbd>V</kbd>.

## Project layout

| File | Role |
|---|---|
| `App.xaml(.cs)` | Startup, tray menu, settings, autostart, Alt hold/tap detection |
| `MainWindow.xaml(.cs)` | Panel UI, clipboard listener, shelf drag & drop, paste, persistence |
| `Native.cs` | Win32 interop (keyboard hook, DWM, acrylic, focus, simulated <kbd>Ctrl</kbd>+<kbd>V</kbd>) |
| `app.ico` | App and tray icon |
| `installer/` | Inno Setup script and the "how to use" page shown by the installer |

## License

[MIT](LICENSE)
