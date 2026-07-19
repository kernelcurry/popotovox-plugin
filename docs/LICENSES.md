# Third-Party License Ledger

Running record of every dependency PopotoVox uses. Updated as components land.

**The project's own license is GPL-3.0** (see `/LICENSE`). Rationale: the voice engines phonemize
through **espeak-ng (GPL-3.0)**, so the combined work is GPL-3.0. Note this is *not* because the plugin
links espeak-ng in-process — it does not. **The plugin binary contains no native code at all**: every
native voice/LLM runtime runs in an **isolated helper process** (`PopotoVox.TtsHost.exe` for Kokoro,
`llama-server` for the casting LLM, the `voxcpm-host` Python host for VoxCPM2), so a native fault can never
crash the game (PRD D10). The plugin itself ships only managed
libraries (NAudio, SharpZipLib). GPL is fully fine for a free, open-source, non-commercial project — it
keeps PopotoVox open for everyone forever. We still avoid **non-commercial / research-only** licenses
(CC-BY-NC, CPML, etc.) — those are the ones that would actually restrict an open release.

**Acceptable licenses:** GPL-3.0 / public-domain / CC0 / CC BY / Apache-2.0 / MIT / BSD.
Still forbidden: anything non-commercial or research-only.

The **About** page (`plugin/Windows/Shell/AboutPage.cs`) renders the same credits in-app; the pinned
downloads live in `plugin/Assets/Manifest.json` (SHA-256 verified, ECDSA-signed). This file is the
human-readable ledger for all three.

---

## Bundled in the plugin (managed, shipped in the release zip)

| Component | Role | License | Attribution |
|-----------|------|---------|-------------|
| Dalamud + plugin APIs | plugin host | per Dalamud terms | follow Dalamud terms |
| NAudio 2.2.1 | audio playback / mixer | MIT | none |
| SharpZipLib 1.4.2 | extract the Kokoro `.tar.bz2` bundle | MIT | none |

The plugin has **no native code** — a voice/LLM fault kills only the helper process, never FFXIV (PRD D10).

## Isolated helper processes (all native code lives here, not in the plugin)

| Helper | Hosts | License(s) |
|--------|-------|-----------|
| `PopotoVox.TtsHost.exe` (our self-contained .NET helper, `tts-host/`) | **sherpa-onnx** TTS runtime (Apache-2.0, k2-fsa) with **espeak-ng** (GPL-3.0) inside the Kokoro bundle | Apache-2.0 + GPL-3.0 |
| `voxcpm-host` (Python, Ultra tier) | **VoxCPM2** — designs a per-NPC voice, then clones it to perform each line | Apache-2.0 |
| `llama-server` (llama.cpp; CPU build, or the CUDA build + `cudart` when a GPU is present) | the casting/emotion LLM | MIT (+ NVIDIA-CUDA for `cudart`) |

espeak-ng runs **inside** the CPU voice helpers (inside the Kokoro bundle, and inside the upstream `piper`
binary), never linked into our plugin code. (VoxCPM2 is tokenizer-free and does not use espeak-ng.)

## Downloaded models & runtimes (pinned in `plugin/Assets/Manifest.json`, SHA-256 verified)

| Id | Component | Role | License | Attribution |
|----|-----------|------|---------|-------------|
| `kokoro-multi-lang-v1_0` | **Kokoro** (via sherpa-onnx) | **default** TTS model — 53 voices / 9 accents, 24 kHz | Apache-2.0 | **attribution** — TTS model by hexgrad; packaged for sherpa-onnx by k2-fsa. Bundle includes espeak-ng data (GPL-3.0). |
| `piper` | **Piper** engine (`rhasspy/piper`, win amd64) | fallback TTS engine — separate child process | MIT | Piper © Rhasspy (MIT). Bundles espeak-ng (GPL-3.0) + ONNX Runtime (MIT) as separate binaries. |
| `libritts-high.onnx` / `.onnx.json` | en_US-libritts-high | Piper TTS voice — 904 speakers, 22.05 kHz | CC BY 4.0 | **attribution required** — trained on LibriTTS. |
| `llama-cuda` | llama.cpp CUDA build (`b9637`) | GPU runtime for the casting/emotion LLM | MIT | llama.cpp © ggml-org / Georgi Gerganov (MIT). |
| `llama-cudart` | NVIDIA CUDA runtime (`cudart`, `b9637`) | optional GPU runtime | NVIDIA-CUDA | freely redistributable; packaged with llama.cpp. |
| `llama-server` | llama.cpp (`b9630`, win cpu x64) | LLM runtime — separate child process | MIT | llama.cpp © ggml-org / Georgi Gerganov (MIT). |
| `qwen2.5-1.5b-instruct-q4` | **Qwen2.5-1.5B-Instruct** (bartowski GGUF, Q4_K_M) | casting + emotion LLM (default) | Apache-2.0 | Qwen2.5-1.5B-Instruct © Alibaba Cloud (Apache-2.0); GGUF quant by bartowski. Keep `NOTICE`. |
| `voxcpm2-runtime` | Portable Python 3.12 env (torch 2.7.1+cu128, voxcpm 2.0.3 + pinned deps) | **Ultra** engine runtime — separate child process | Aggregate-Permissive | Aggregate of unmodified upstream packages, each individually permissive: CPython (PSF), torch (BSD-3-Clause, bundles NVIDIA CUDA redistributables), VoxCPM © OpenBMB (Apache-2.0), etc. Per-package license texts ship inside the zip under `LICENSES/`; build recipe = `tools/voxcpm-runtime-requirements.txt`. Hosted at `huggingface.co/kernelcurry/popotovox-voxcpm2-runtime`. |
| `voxcpm2-model` | **VoxCPM2** model snapshot (repack) | **Ultra** voice model — design + clone, 48 kHz | Apache-2.0 | VoxCPM2 © OpenBMB (Apache-2.0), repacked **unmodified** from the upstream `openbmb/VoxCPM2` snapshot as one pinned-checksum zip (LICENSE + attribution inside). Hosted at `huggingface.co/kernelcurry/popotovox-voxcpm2-model`; loaded fully offline. |

**Ultra (VoxCPM2)** is credited on the About page; the host script (`voxcpm-host/voxcpm2_host.py`) ships in
the plugin zip, and the runtime + model above install via the signed manifest. A hand-installed dev config
(`voxcpm-dev.json`) can still override the packaged layout for development.

Note: signing the manifest uses ECDSA P-256 / SHA-256; the public key is embedded in
the plugin, the private key is kept out of the repo (`.signing/`, gitignored).

---

## LLM default — resolved (PRD §14 open item)

The PRD floated **Qwen2.5-3B (Apache-2.0)** as the default. Build-time research found
that is **wrong**: in the Qwen2.5 family the **3B is `qwen-research` (non-commercial)** —
forbidden by §10.2. The Apache-2.0 sizes are 0.5B / 1.5B / 7B / 14B / 32B.

**Default = Qwen2.5-1.5B-Instruct (Apache-2.0)** — smallest, most CPU-friendly
(PRD §11), genuinely permissive. Documented opt-in alternatives (not bundled):
Qwen2.5-7B-Instruct (Apache-2.0, higher quality, ~4.5 GB) and Phi-3.5-mini-instruct
(MIT, ~3.8B).

---

## Rejected / forbidden (per PRD §10.2)

- **libritts_r** / any lessac-lineage voice — fine-tuned from lessac → Blizzard "research purposes only."
- **XTTS v2** — Coqui CPML; commercial use requires a separate agreement.
- **F5-TTS** — CC-BY-NC; non-commercial only.
- **Llama community-license LLMs** as the bundled default (opt-in only, never the default).
- Anything whose card says "research only" / "non-commercial" / requires payment.

---

## Change log

- **2026-06-13** — file created; initial planned ledger entered from PRD §10.1.
- **2026-06-13 (later)** — D10 simplified to native binaries. Python runtime removed from planned ledger; `llama-server` binary added; espeak-ng note updated (boundary is inside the upstream `piper` binary, not adjacent to our code).
- **2026-06-14** — M1–M4 landed. Ledger moved from "planned" to "in use" with pinned versions + SHA-256. **LLM default corrected to Qwen2.5-1.5B-Instruct (Apache-2.0)** after finding Qwen2.5-3B is `qwen-research` (non-commercial, forbidden). NAudio (MIT) added for audio playback. License allowlist now enforced in code (`LicensePolicy`, fail-closed at manifest load + before every download).
- **2026-06-30** — ledger brought current to the shipped **four-engine ladder** for the 0.9 release. **Corrected the Kokoro/sherpa-onnx entry** from "native, in-process" to the isolated `PopotoVox.TtsHost.exe` child process (the plugin has no native code — PRD D10). **Added** the previously-missing components: Orpheus-3B (Apache-2.0), the SNAC decoder (MIT), the llama.cpp CUDA build + `cudart` (MIT / NVIDIA-CUDA), and the Studio-tier OmniVoice + CosyVoice 3 (both Apache-2.0). Restructured into bundled / helper-process / downloaded groups; reconciled against `plugin/Assets/Manifest.json` + `AboutPage.cs`. `Manifest.json` itself unchanged (no re-sign).
- **2026-07-02** — reconciled to the **engine-ladder overhaul**: Orpheus + Studio engines deleted, **VoxCPM2 is Ultra** (`openbmb/VoxCPM2`, Apache-2.0, `voxcpm-host`). **Removed** the `orpheus-3b-q4` + `snac-decoder` assets from `Manifest.json` and **re-signed** it (ECDSA P-256/SHA-256; verified against the embedded public key); dropped their About-page credits and the Orpheus-3B-lineage note (moot with Orpheus gone). The `llama-cuda`/`llama-cudart` CUDA runtime now serves the **casting LLM** on GPU (its manifest `kind` is still the legacy `OrpheusRuntime` — a harmless misnomer; retag deferred). VoxCPM2 has no signed-manifest assets yet (runs from dev config, like Studio did).
