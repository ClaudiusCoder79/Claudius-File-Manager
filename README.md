# Claudius File Manager

[![Build](https://github.com/ClaudiusCoder79/Claudius-File-Manager/actions/workflows/build.yml/badge.svg)](https://github.com/ClaudiusCoder79/Claudius-File-Manager/actions/workflows/build.yml)
![Platform](https://img.shields.io/badge/platform-Windows%2011%20x64-0078D4)
![.NET](https://img.shields.io/badge/.NET-8-512BD4)
![License](https://img.shields.io/badge/license-Freeware-34C759)

A free dual-pane file manager for Windows 10/11 with **Total Commander's** two-panel layout and keyboard workflow, a colourful **Claudius ribbon** (like Claudius PDF Editor), **System / Light / Dark** themes and six languages (English, Polski, Deutsch, Français, Italiano, Español).

**Freeware** · Created by **Michal Czapla & Claude AI**

<!-- Add a screenshot: save it as docs/screenshot.png and uncomment the line below -->
<!-- ![Claudius File Manager](docs/screenshot.png) -->

## Download

Get the latest version from the [Releases](https://github.com/ClaudiusCoder79/Claudius-File-Manager/releases) page:

- **`ClaudiusFileManager-Setup-x.y.z.exe`**: the installer. Adds Start menu and optional desktop shortcuts, an optional **Open in Claudius File Manager** entry on the folder right-click menu, and an uninstaller in Settings → Apps.
- **`ClaudiusFileManager.exe`**: the portable version. A single self-contained file; just run it, nothing to install.

The installer lets you install for just yourself (no admin needed) or for everyone on the PC. Installing a newer version upgrades the old one in place.

Windows SmartScreen may warn about an unsigned app the first time; choose **More info → Run anyway**.

## Features

**Total Commander layout**
- Two file panels with the active one outlined in blue; **Tab** switches between them
- Drive buttons above each panel, a path field, back/up and a status bar with item counts, selected size and free space
- Name / Ext / Size / Modified columns; click a header to sort (folders first, Explorer-style natural sort)
- Function-key bar: F2 Rename, F3 View, F4 Edit, F5 Copy, F6 Move, F7 New Folder, F8 Delete

**File operations**
- Copy, move and delete go through the Windows shell: native progress dialog, overwrite/skip prompts, Undo in Explorer and the Recycle Bin
- Ctrl+C / Ctrl+X / Ctrl+V clipboard that works both ways with Explorer
- Drag and drop between panels, onto folders, and to/from Explorer or the desktop (hold Shift to move)
- F3 viewer for text and images; F3 on a folder calculates its size
- **Preview window:** on by default — a small floating window that follows the selected file: video and music player, photos, PDF pages, text and CNC programs, and Windows thumbnails for Office documents and everything else. Turn it off or on with the Preview button (Home or View tab) or Ctrl+Q
- **ZIP archives:** Zip (Alt+F5) packs files and folders, Unzip (Alt+F9) unpacks them; press Enter on a .zip to browse it like a folder — preview, open and copy files out with F5, copy files in, or delete them from the archive
- Live auto-refresh when files change on disk
- Panels, hidden-files setting and window size are remembered between runs

## Keyboard shortcuts

| Key | Action |
|---|---|
| Tab | Switch panel |
| Enter / Backspace / Alt+← | Open / parent folder / back |
| Insert | Mark item and move down (Ctrl/Shift+click also work) |
| Type letters | Jump to a file by name |
| F2 or Shift+F6 | Rename |
| F3 | View file / folder size |
| F4 / Shift+F4 | Edit in Notepad / new text file |
| F5 / F6 | Copy / move to other panel |
| F7 | New folder |
| F8 or Del / Shift+Del | Recycle Bin / delete permanently |
| Ctrl+F / Ctrl+L | Search filter / path field |
| Ctrl+R | Refresh |
| Ctrl+H | Toggle hidden files |
| Ctrl+U / Ctrl+= | Swap panels / same folder in other panel |
| Ctrl+T / Ctrl+E | Terminal here / show in Explorer |
| Ctrl+Shift+C | Copy full path |
| Ctrl+Q | Preview window on/off |
| Alt+F5 / Alt+F9 | Zip / unzip |
| F1 | Shortcut list |

## Building from source

Requirements: Windows 10/11 x64 and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (`winget install Microsoft.DotNet.SDK.8`).

```bat
git clone https://github.com/ClaudiusCoder79/Claudius-File-Manager.git
cd Claudius-File-Manager
build.bat
```

The exe lands in `publish\ClaudiusFileManager.exe`. To develop in Visual Studio 2022, open `ClaudiusFileManager.sln` and press F5.

### Building the installer

Also install [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`), then run:

```bat
build-installer.bat 1.0.0
```

The installer lands in `installer-output\ClaudiusFileManager-Setup-1.0.0.exe`. The script is `installer\ClaudiusFileManager.iss`.

## Releasing

Every push to `main` builds the app on GitHub Actions and attaches the installer and portable exe as downloadable artifacts (test builds are numbered 1.0.<run>).

To publish a **Release** (public download page with `ClaudiusFileManager-Setup-x.y.z.exe` and the portable exe), do any one of these:

- On GitHub, open **Actions → Build → Run workflow**, type the version (for example `1.2.0`) and click **Run workflow**.
- Push a commit whose message contains a line `release v1.2.0`.
- Push a tag: `git tag v1.2.0 && git push origin v1.2.0`.

## Project layout

```
Claudius-File-Manager/
├─ .github/workflows/build.yml   CI build + releases
├─ src/ClaudiusFileManager/
│  ├─ Themes/                    Light.xaml, Dark.xaml colours + Styles.xaml (ribbon, rows, menus)
│  ├─ Languages/                 Translation files: en, pl, de, fr, it, es (.json)
│  ├─ MainWindow.xaml(.cs)       Title bar, toolbar, panels, F-key bar, shortcuts
│  ├─ FilePanel.xaml(.cs)        One panel: listing, sorting, filter, drag & drop
│  ├─ ShellOps.cs                Copy / move / delete via the Windows shell
│  ├─ IosDialog.cs               iOS-style alerts and prompts
│  ├─ ViewerWindow.cs            F3 viewer
│  ├─ FileItem.cs, Settings.cs   Row model, icons, saved settings
│  ├─ Assets/app.ico             App icon
│  └─ app.manifest               DPI awareness, long paths, modern dialogs
├─ installer/ClaudiusFileManager.iss     Windows installer (Inno Setup)
├─ build.bat                     One-click local build
├─ build-installer.bat           Build exe + installer
└─ ClaudiusFileManager.sln
```

You can also open a folder directly: `ClaudiusFileManager.exe "D:\Projects"`.

Settings are stored at `%AppData%\ClaudiusFileManager\settings.json`.

## License

[MIT](LICENSE)

*Not affiliated with Ghisler Software. "Total Commander" is a trademark of its respective owner and is mentioned only as a design reference.*
