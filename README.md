# SBG Mod Switcher (source code)

Source code of **SBGModSwitcher.exe**, the app in the Nexus mod
[Straight To Tyr](https://www.nexusmods.com/godofwarragnarok/mods/292) for God of War Ragnarök (PC).

The app switches mods on and off. It builds each modded file from the player's own game files
(patch recipes), so it ships **no game files**. It needs the mod loader GoWR MIO.

This repo holds only the app's code. The mod data (recipes, Lua plug-in) is in the release zip on Nexus.

## Build

Needs Windows and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```
dotnet publish src/ModSwitcher2.csproj -c Release -r win-x64 --self-contained true -o out -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=false -p:DebugType=None -p:DebugSymbols=false -p:ContinuousIntegrationBuild=true -p:Deterministic=true
```

The result is `out/SBGModSwitcher.exe` plus `out/clrgc.dll`. These are the same two files that are in the release zip.

## Layout

- `src/`: the WPF app (C#, .NET 8).
- `switcher/GowrTools.cs`: shared helpers (LZ4, wad size table, hashes). The `.csproj` links it in.

The experimental Lab (`src/Lab.cs`, `src/LabView.cs`) is a stub here. Releases never show it.

## Licence

MIT, see `LICENSE.txt`. Credits: `CREDITS.md`.
