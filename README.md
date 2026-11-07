# Wallpaper Studio

A tiny, portable multi-monitor wallpaper editor for Windows 10/11.

Lay out a different image on each monitor, **crop**, **zoom** and **move** them on a canvas that matches your real monitor arrangement, then apply the result with one click.

- Single ~30 KB `.exe`: no installer, no admin rights, no dependencies (uses the .NET Framework that ships with Windows)
- Canvas is built from your actual monitor layout (sizes, positions, portrait/landscape)
- Add any number of images and stack them
- **Fill / Fit / Stretch / Center** for any monitor, or span one image across all monitors
- Drag to move (snaps to monitor edges), mouse wheel to zoom, corner handles to resize
- **Crop mode** with edge and corner handles
- Drag an image onto another monitor to move it there
- Works even where Windows Settings doesn't offer "Set for monitor 1 / 2"

## Use it

1. Download `WallpaperStudio.exe` from the [Releases](../../releases) page and run it. Windows SmartScreen may warn about an unsigned app: choose *More info > Run anyway*.
2. **Add images...** and select one or more files.
3. Pick the target monitor in the right-hand panel, then **Fill / Fit / Stretch / Center**.
4. Adjust by dragging, scrolling, or toggling **Crop mode**.
5. **Set as wallpaper**.

| Action | How |
| --- | --- |
| Move image | Drag |
| Zoom | Mouse wheel, or drag a corner handle |
| Crop | Toggle *Crop mode*, drag edge/corner handles |
| Disable snapping | Hold `Alt` while dragging |
| Remove image | `Del` or *Remove* |
| Export without applying | *Save PNG...* |

## How it works

The app renders every layer into one bitmap the size of your whole virtual desktop and applies it as a **Span** wallpaper (`WallpaperStyle=22`). Renders are saved in `%APPDATA%\MultiMonitorWallpaper` (old ones are cleaned up). Nothing else is installed or modified.

## Build from source

Needs only Windows (the C# compiler in `C:\Windows\Microsoft.NET\Framework64\v4.0.30319` is used):

```powershell
.\build.ps1
```

The result is `bin\WallpaperStudio.exe`.

## License

MIT, see [LICENSE](LICENSE).
