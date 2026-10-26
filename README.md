# SBG Mod Switcher (source code)

Source code of **SBGModSwitcher.exe**, a mod installer for God of War Ragnarök (PC) mod packs.

You download the app once. Then you install mod packs with it (for example
[Straight To Tyr](https://www.nexusmods.com/godofwarragnarok/mods/292)). A mod pack is a data-only zip: patch
recipes and Lua files, no programs. The app checks each pack, shows what it will change, and turns its mods on
and off. It builds each modded file from the player's own game files, so it ships **no game files**. It needs the
mod loader GoWR MIO.

This repo holds only the app's code. It holds no mods, no mod packs and no game data.

## Build

Needs Windows and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```
dotnet publish src/ModSwitcher2.csproj -c Release -r win-x64 --self-contained true -o out -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=false -p:DebugType=None -p:DebugSymbols=false -p:ContinuousIntegrationBuild=true -p:Deterministic=true
```

The result is `out/SBGModSwitcher.exe` plus `out/clrgc.dll`. These are the same two files that are in the release zip.

## Layout

- `src/`: the WPF app (C#, .NET 8).
  - `PackManifest.cs`, `PackInstaller.cs`, `Packs*.cs`: check, preview, install, update and remove mod packs.
  - `GuideView.cs`: the first-start guide (game folder, GoWR MIO, first mod).
  - `Apply.cs`, `Recipe.cs`: turn mods on and off from patch recipes.
- `switcher/GowrTools.cs`: shared helpers (LZ4, wad size table, hashes). The `.csproj` links it in.

The experimental Lab (`src/Lab.cs`, `src/LabView.cs`) is a stub here. Releases never show it.

## Licence

MIT, see `LICENSE.txt`. Credits: `CREDITS.md`.

The licence covers the SBG Mod Switcher's own code and files only. It gives no rights to God of War Ragnarök or any
of its files, which belong to Sony Interactive Entertainment. The app builds its mods from the user's own game files.
