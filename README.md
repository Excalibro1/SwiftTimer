# SwiftTimer

SwiftTimer is an early SwiftlyS2 surf timer for Counter-Strike 2. It supports common surf map trigger naming, local JSON records, map tier metadata, player commands, and a particle HUD rendered through CenterHud.

## Requirements

- CS2 dedicated server
- SwiftlyS2
- CenterHud installed
- A client-mounted workshop addon containing the particle glyph assets

## Install

Publish the project and copy the output to:

```text
addons/swiftlys2/plugins/SurfTimer/
```

Build:

```powershell
dotnet publish SurfTimer.csproj -c Release
```

## Commands

- `!r` / `!restart` - restart at the main start.
- `!b <number>` / `!bonus <number>` - teleport to a bonus start.
- `!s <number>` / `!stage <number>` - teleport to a stage start.
- `!cp <number>` - teleport to a checkpoint.
- `!stop` - stop the current run.
- `!pb` - show personal best.
- `!wr` / `!top` - show top records.
- `!timer` - show timer status.
- `!timerhud` - open particle HUD editor.
- `!zones` - rescan and print map trigger zones.
- `!showzones` / `!hidezones` - show or hide zone debug labels.

## Zone Naming

SwiftTimer auto-detects common trigger names including:

```text
map_start
map_end
s1_start
s1_end
stage1_start
stage1_end
b1_start
b1_end
bonus1_start
bonus1_end
b1_cp1
map_cp1
```

Use `!zones` on a map to print matched and unmatched trigger names.

## Records

Records are stored locally:

```text
data/plugins/SurfTimer/records.json
```

## Status

This is an early public build. The timer works, but the API and config format may still change.
