# hdt-wine-overlay-helper

A small companion program for [Hearthstone Deck Tracker](https://github.com/HearthSim/Hearthstone-Deck-Tracker) under Wine or Proton on Linux. When Hearthstone loses focus, it hides HDT's overlay, so the overlay no longer stays drawn on top of every other window. It also keeps the overlay on the Hearthstone window when you move or resize it.

It replaces my [hdt-wine-overlay-hide](https://github.com/emaspa/hdt-wine-overlay-hide) plugin. HDT [drops plugin support on November 30th 2026](https://help.hearthsim.net/en/articles/18470784-what-s-changing-about-plugins-in-hdt), so this is a standalone program that runs next to HDT in the same Wine prefix. Version numbers continue from the plugin and start at 2.0.0.

## The problem

When Hearthstone goes to the background, HDT doesn't hide its overlay. It sends the overlay to the bottom of the window stack and leaves it drawn. That works on Windows.

Under Wine, the overlay is a no-activate, topmost, click-through window, so Wine creates it as an override-redirect X window. The compositor (KWin, Mutter, etc.) doesn't manage override-redirect windows and always draws them above normal windows. HDT's "send to back" therefore does nothing, and the overlay floats over your browser, terminal and everything else until you click back into Hearthstone.

Wine isn't at fault here. I logged `GetForegroundWindow()` while switching apps, and it moved to the desktop window about a second after Hearthstone lost focus. HDT sees the change and handles it in a way that only works on Windows, so no Wine setting fixes it.

There is a second problem. HDT learns that the Hearthstone window moved from a location-change WinEvent hook. Under Wine that event doesn't reach HDT when the window manager moves the window, so the overlay stays where it was when the game started.

## What the program does

Every 100 ms it checks which window is in the foreground. If Hearthstone is running but isn't the foreground window, it hides HDT's overlay window with `ShowWindow`, and Wine unmaps the X window. When Hearthstone gets focus back, it shows the overlay again.

It follows the "Hide in background" option in HDT's overlay settings, which it reads from HDT's `config.xml`. With that option off, the program doesn't hide the overlay.

On the same 100 ms tick it compares the overlay's position and size with Hearthstone's client area. If they differ, it moves the overlay there with `SetWindowPos`, using the same rectangle HDT computes.

It only uses window calls (`FindWindow`, `GetForegroundWindow`, `ShowWindow`, `SetWindowPos`), and the only window it changes is HDT's overlay. It doesn't read or write the memory of Hearthstone or HDT and doesn't inject anything into either.

The program exits by itself about 15 seconds after HDT closes.

## Install

Download `WineOverlayHide.exe` from the [releases page](../../releases), or build it from source as described below.

Put the exe anywhere inside the Wine prefix where HDT runs. `build.sh --install` uses `drive_c/users/<user>/AppData/Local/WineOverlayHide/`. Under Proton the user is usually `steamuser`.

It needs .NET Framework 4, which the prefix already has because HDT needs it too.

## Run it

Point your HDT launcher at `WineOverlayHide.exe --launch` instead of at HDT. The program starts HDT and then watches the overlay.

In Lutris, edit the HDT entry and set:

- Executable: `<prefix>/drive_c/users/steamuser/AppData/Local/WineOverlayHide/WineOverlayHide.exe`
- Arguments: `--launch`

If you'd rather keep your launcher as it is, start `WineOverlayHide.exe` without arguments in the same prefix after HDT. It waits up to three minutes for HDT to appear. A second copy exits right away, so starting it twice does no harm.

`--log [file]` writes focus changes and hide/show events to a file. The default is `WineOverlayHide.log` in the prefix's temp folder. Use it when the overlay doesn't behave.

## Coming from the plugin

Delete `WineOverlayHide.dll` from `AppData/Roaming/HearthstoneDeckTracker/Plugins/` in the prefix. From November 30th HDT ignores it anyway.

## Build from source

`build.sh` runs the .NET Framework `csc.exe` that HDT's prefix already contains. You don't need a .NET SDK on the Linux side.

Plain Wine:

```sh
WINEPREFIX=~/path/to/prefix ./build.sh --install
```

Proton through umu (for example a Lutris prefix):

```sh
WINEPREFIX=~/path/to/prefix \
PROTONPATH=/usr/share/steam/compatibilitytools.d/proton-cachyos-slr \
GAMEID=umu-battlenet \
./build.sh --install
```

Use the same `PROTONPATH` and `GAMEID` as the launcher that runs HDT. This lets the build join the running wineserver instead of starting a second one. Close the program before you rebuild with `--install`, because the copy overwrites the running exe.

## Limitations

- The overlay also hides when you focus HDT's own main window, because Hearthstone isn't the foreground window then either.
- The plugin could tell menus from matches and honoured HDT's separate "hide in background" setting for the menu overlay. The standalone program can't see that from outside, so the one "Hide in background" option covers both.
- The overlay disappears about a second after you switch away. That is how long Wine takes to report the focus change.
- While you drag the Hearthstone window, the overlay trails it by up to a tenth of a second.
- If you kill the program while the overlay is hidden, start it again and click into Hearthstone. It takes over the hidden overlay and shows it. Restarting HDT or Hearthstone also brings the overlay back.
- Tested with HDT 1.60.7 on KDE Plasma (Wayland, XWayland) with proton-cachyos. Other compositors should behave the same, since every compositor stacks override-redirect windows on top.

## License

GPL-3.0-or-later. See [LICENSE](LICENSE).
