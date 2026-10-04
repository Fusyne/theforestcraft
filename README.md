# ForestCraft

Play **The Forest** with a **Minecraft** body: physics, sprinting, jumping, your hand, hotbar,
inventory, chat, blocks, mobs and combat all come from a hidden Minecraft 26.2 (Fabric)
running in the background, while you see the world of The Forest. Inspired by
[SkyCraft](https://github.com/chasmlol/SkyCraft).

> Single-player only for now. Windows only.

## Requirements

- **The Forest** (Steam, PC)
- **Minecraft: Java Edition** (a Microsoft account that owns the game)
- Nothing else: the installer takes care of BepInEx, Prism Launcher, the Minecraft 26.2 +
  Fabric instance and Java 25.

## Installation (players)

1. Download `ForestCraft-x.y.z.zip` from the **Releases** page and extract it.
2. Double-click **`install.bat`**.
3. First time only: in Prism Launcher, add your Microsoft account
   (top right → *Manage Accounts*) and launch the **ForestCraft** instance once so Prism
   downloads Minecraft and Java (it may close on its own, that's expected).
4. Start **The Forest** from Steam. Minecraft starts by itself in the background.

To update: download the new release and run `install.bat` again.
To reset everything (Minecraft world + dug terrain): `reset.bat`
(your The Forest saves are not touched).

What `install.bat` does:

| Step | Details |
|---|---|
| The Forest | found through Steam (all libraries), otherwise it asks for the folder |
| BepInEx 5.4.23.2 x64 | downloaded from GitHub and installed into the game folder if missing |
| Plugin | `ForestCraft.dll` → `The Forest\BepInEx\plugins` |
| Prism Launcher | detected, otherwise installed with `winget` (or opens the download page) |
| Instance | `ForestCraft` created: Minecraft 26.2 + Fabric Loader 0.19.3, automatic Java |
| Mod | `forestcraft.jar` → the instance's `mods` folder |

## Controls

| Key | Action |
|---|---|
| WASD (ZQSD on AZERTY) / Space | move / jump (Minecraft physics) |
| Ctrl | sprint · Shift / C: sneak |
| Left click | break / hit (trees, bushes, suitcases, cannibals…) |
| Right click | place / use |
| 1–9, mouse wheel | hotbar |
| E | pick up / interact in The Forest (picked-up items become Minecraft items) |
| I | Minecraft inventory |
| T, / | chat, command |
| F5 | third-person view |

## Building from source (developers)

Requirements: The Forest installed, the [.NET SDK](https://dotnet.microsoft.com/download) (6 or newer).
JDK 25 is downloaded automatically into `.tools\` if none is found.

- `build.bat` → builds both halves and creates `dist\ForestCraft-<version>.zip` (the zip to
  attach to a GitHub release).
- `install.bat` run from the repository → builds, then installs directly.

Layout:

```
forest/    BepInEx plugin for The Forest (C#, net35)
fabric/    Fabric mod for Minecraft 26.2 (Java 25)
scripts/   installer, build, reset (PowerShell)
```

The two games talk through shared memory (`%LOCALAPPDATA%\ForestCraft\link.bin`).

## Publishing a release

```bat
build.bat
git tag v0.1.0 && git push origin v0.1.0
gh release create v0.1.0 dist\ForestCraft-0.1.0.zip --title "ForestCraft 0.1.0" --notes "First release"
```

## License

MIT — see [LICENSE](LICENSE). This project contains no files from The Forest or Minecraft;
you need to own both games. Not affiliated with Endnight Games, Mojang or Microsoft.
