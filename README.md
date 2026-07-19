# PopotoVox

> *"Every popoto gets a voice."*

A self-contained FFXIV Dalamud plugin that gives every NPC a distinct, locally-cast synthetic voice. No cloud, no paid services, no dependency on other plugins.

By **[kernelcurry](https://kernelcurry.com)**.

## Status

**Working end-to-end and confirmed in-game** (release polish under way). Full pipeline:
dialogue capture → NPC identity resolution → rules + local-LLM casting → local TTS, with a
management UI (NPC browser, bio cards, override editor, presets) and a signed, SHA-256-verified
asset downloader.

Three selectable voice engines (a quality ladder, chosen via Settings or the **Low/Medium/High/Ultra**
presets):

| Engine | Preset | Hardware | Notes |
|---|---|---|---|
| **Piper** | Low / Medium | CPU | Lightweight/fast, 904 voices, more robotic (Medium adds smart casting) |
| **Kokoro** (default) | High | CPU | Natural, 53 voices across 9 real-world accents, no GPU needed |
| **VoxCPM2** | Ultra | NVIDIA GPU | Most lifelike — one model that **designs a unique voice per NPC** (in the speaker's native tongue, for a real accent) then **clones it to perform each line** with directed emotion; **streaming playback** so speech starts almost immediately; cached per NPC. The heaviest tier: a large one-time download (GPU runtime + model) |

A small local LLM ("smart casting") picks a fitting voice per NPC and, on Ultra, directs
restrained mood-appropriate emotion per line. First run launches a guided **setup wizard** (choose a voice →
optional smart casting → download → done); or open `/popotovox` → **Storage** and install an engine, then
talk to NPCs.

**Ambient world chatter (optional, off by default):** beyond interactive dialogue, PopotoVox can voice the
**overhead speech bubbles** NPCs say spontaneously as you walk by. These fade with distance using a realistic
acoustic falloff (modelled on how real speech attenuates) and can **swell and fade in real time as you move
past** the speaker — so a busy hub feels alive. Multiple bubbles can **play at once** through a real audio
mixer, each at its own distance volume; during a conversation, nearby chatter politely **ducks** rather than
going silent. Tunable hearing distance and overlap limit, and matched to each NPC's voice like every other line.

The management UI has a modern, card-based **NPC browser** that shows each character's identity, full
gear, and a transparent "why this voice" breakdown (in the casting director's own words). 

## Repo layout

The release repository for the plugin — the minimum needed to build, verify and install it.
(Development happens elsewhere; this repo tracks what ships.)

- `plugin/` — the Dalamud plugin (C#, .NET 10, Windows; built via `Dalamud.NET.Sdk/15.0.0`).
- `tts-host/` — the isolated TTS helper process (Kokoro via sherpa-onnx) the plugin launches.
- `voxcpm-host/` — the isolated VoxCPM2 (Ultra) Python host script.
- [`docs/LICENSES.md`](docs/LICENSES.md) — third-party dependency ledger.
- `images/` — the plugin icon.
- `Dockerfile`, `build.sh` — Docker-based build (no host .NET SDK needed).
- `.github/workflows/build.yml` — CI: builds on every push, releases on `v*` tags.
- `repo.json` — Dalamud custom-repo manifest.

## Install in-game (preferred — via Dalamud custom repo)

This is the one-click path. You add a URL to Dalamud once and the Plugin Installer treats PopotoVox like any other plugin.

1. In FFXIV: `/xlsettings` → **Experimental** → **Custom Plugin Repositories**.
2. Add this URL and check the enabled box:
   ```
   https://raw.githubusercontent.com/kernelcurry/popotovox-plugin/main/repo.json
   ```
3. Save and close. Open the Plugin Installer, find **PopotoVox**, install.

Custom-repo installs only work once a GitHub release exists. The CI workflow publishes a release whenever a `v*` tag is pushed (e.g. `git tag v0.9.0 && git push origin v0.9.0`).

## Build locally (no .NET SDK required, uses Docker)

If you'd rather build from source — for example to test an unreleased commit — use the Docker build. You only need Docker installed.

```sh
./build.sh
```

Output: `./dist/PopotoVox/` containing `PopotoVox.dll`, `PopotoVox.json`, and dependencies.

Then in FFXIV:
1. `/xlsettings` → **Experimental** → **Dev Plugin Locations**.
2. Add the absolute path printed by `build.sh` (e.g. `/path/to/PopotoVox/dist`).
3. Plugin Installer → **Dev Tools** tab → enable **PopotoVox**.

The Docker build downloads Dalamud reference DLLs from the official distribution channel and targets `net10.0-windows` from a Linux container via `EnableWindowsTargeting`. No Windows host required.

## Build locally (with .NET 10 SDK)

If you have the SDK installed and prefer a native build:

```sh
cd plugin
dotnet restore -p:EnableWindowsTargeting=true
dotnet build  -p:EnableWindowsTargeting=true
```

`EnableWindowsTargeting=true` is only needed when building on non-Windows hosts (Linux / macOS) — on Windows it's a no-op. Dalamud reference DLLs are resolved by `Dalamud.NET.Sdk` from:

- Windows: `%AppData%\XIVLauncher\addon\Hooks\dev\` (default)
- Linux:   `$HOME/.xlcore/dalamud/Hooks/dev/` (default)
- Override anywhere: set `DALAMUD_HOME=/path/to/extracted/dalamud-distrib/`

## In-game commands

| Command | Action |
|---|---|
| `/popotovox` (alias `/pvox`) | Open the main window (NPCs) |
| `/popotovox config` | Jump to Settings |
| `/popotovox setup` | Reopen the first-run setup wizard |
| `/popotovox diag` | Jump to the System dashboard — live pipeline activity, status, and debug info |

## License

**GPL-3.0** (see [`LICENSE`](LICENSE)). Free and open-source for everyone, forever. The
default voice engine (Kokoro via sherpa-onnx) links GPL-3.0 espeak-ng, so the combined
work is GPL-3.0. See [`docs/LICENSES.md`](docs/LICENSES.md) for the full dependency audit —
we still avoid all non-commercial / research-only licenses.

## Author

[kernelcurry](https://kernelcurry.com) · info@kernelcurry.com
