# Fallout: New Vegas Modification Framework - installer source

C# source of the installer, uninstaller and helper that ship with the Fallout: New Vegas Modification Framework mod pack (Nexus: ADD LINK). The pack itself (mods, BIN parts, guide) is **not** in this repository; every mod in it belongs to its authors.

The code was written largely with AI assistants (Claude and ChatGPT) under my direction.

## What the installer does
- Validates your own copy of the game (Steam, GOG or Epic) and refuses to continue on a mismatch.
- Unpacks the pack from the numbered `Fallout Mod Setup-NN.bin` parts into a new mod folder, in parallel on SSDs, and verifies every file by hash.
- Rebuilds the patched game masters from your own files with xdelta (no game files are shipped), patches a scratch copy of `FalloutNV.exe` for 4 GB, installs missing Visual C++ / DirectX runtimes from the `add-software` folder.
- Backs up every game file it changes, keeps a change journal and can undo everything (`UninstallSupport.cs`), including a "restore only" mode.
- One elevated step for runtimes and optional, limited Microsoft Defender file exclusions; no network access.

## Layout
- `src/` - the program (`Installer.cs` engine, `Wizard.cs` UI, `UninstallSupport.cs`, `PortableSupport.cs`, `UpdateSupport.cs`, `DefenderSupport.cs`, `Integrations.cs`, ...), `*Checks.cs` self-check programs, `prerequisites.json`, `english-packs.json`
- `build.ps1` - compiles with the .NET Framework 4 `csc.exe` that ships with Windows
- `assets/` - images the build embeds (not included, see `assets/README.md`)

## Building
You also need a `package.json` (the manifest of files, hashes and patches of the mod pack, which the release EXE embeds). It is generated from the pack and is not in this repository, so a full working installer cannot be built from this source alone.

```powershell
.\build.ps1 -Package path\to\package.json
```

The `*Checks.cs` files are standalone programs that exercise the engine against temporary fixtures; compile each together with the sources the same way.

## Third-party tools it drives
Mod Organizer 2, xNVSE, the FNV 4GB patcher, xdelta3, FNVEdit and the Microsoft runtimes are used as separate programs and keep their own licenses. Check them before redistributing anything.

## License
ADD A LICENSE (the author has to choose one).
