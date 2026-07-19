#!/usr/bin/env python
"""VoxCPM2 host for the PopotoVox Ultra engine — ONE persistent stdin-loop process that both DESIGNS a
per-NPC reference voice (native-tongue text → accented voice) and CLONES it to speak each line (optionally
with a per-line "(style)" emotion prefix). Mirrors the Studio host protocol so the C# VoxCpmHostProcess is
a near-copy of CosyVoiceHostProcess.

Protocol: at startup prints one line "SR <hz>" (48000 — the load barrier + PCM sample rate). Then one JSON
request per line, and one response back:
  design: {"cmd":"design","desc":"<voice description>","refText":"<native-tongue reference line>",
           "seed":<int>,"outputFile":"C:/.../ref.wav"}   → writes the reference WAV, prints its path
  clone:  {"cmd":"clone","refWav":"C:/.../ref.wav","text":"<english line>",
           "style":"<optional style phrase>","outputFile":"C:/.../out.wav"}  → writes WAV, prints its path
  stream: {"cmd":"stream","refWav":"C:/.../ref.wav","text":"<english line>","style":"<optional>"}
           → streams PCM16 chunks as BINARY frames on stdout so the line starts playing before it finishes:
             each frame = 4-byte big-endian int32 length N, then N bytes of little-endian PCM16;
             length 0 = clean end, length -1 = error. No text line for a stream request.
For design/clone an empty text line back = error. stdout carries ONLY response signals (text lines for
design/clone, binary frames for stream); ALL logs go to stderr. design/clone output is peak-normalized;
streamed chunks use a fixed gain (per-chunk peak-normalize would pump volume) — VoxCPM2 runs hot. Run with
the VoxCPM2 venv's python.
"""
import sys
import os
import json
import time
import struct

import numpy as np
import soundfile as sf
import torch
from voxcpm import VoxCPM

MODEL = os.environ.get("VOXCPM_MODEL", "openbmb/VoxCPM2")
STREAM_GAIN = 0.8  # fixed attenuation for streamed chunks (can't peak-normalize a chunk in isolation)


def log(msg):
    print(msg, file=sys.stderr, flush=True)


def start_parent_watchdog():
    """Exit if the parent (the game) dies, so a crash never leaks this GPU process. Windows-only."""
    pid = os.environ.get("VOXCPM_PARENT_PID")
    if not pid:
        return
    try:
        ppid = int(pid)
    except ValueError:
        return
    import ctypes
    import threading
    import time as _t
    k32 = ctypes.windll.kernel32
    QUERY = 0x1000          # PROCESS_QUERY_LIMITED_INFORMATION
    STILL_ACTIVE = 259

    def alive():
        h = k32.OpenProcess(QUERY, False, ppid)
        if not h:
            return False
        code = ctypes.c_ulong()
        ok = k32.GetExitCodeProcess(h, ctypes.byref(code))
        k32.CloseHandle(h)
        return bool(ok) and code.value == STILL_ACTIVE

    def loop():
        while True:
            _t.sleep(2.0)
            if not alive():
                log("[watchdog] parent process gone — exiting to free the GPU")
                os._exit(0)

    threading.Thread(target=loop, daemon=True).start()


def norm(wav):
    a = np.asarray(wav, dtype=np.float32).squeeze()
    return a / (np.abs(a).max() + 1e-9) * 0.95  # VoxCPM2 output runs hot → peak-normalize or it clips


def stream_clone(model, ref_wav, text):
    """Clone speaking `text` and stream PCM16 chunks as length-prefixed binary frames on stdout.buffer.
    Each frame: >i (big-endian int32) byte-count, then the PCM16 bytes. Ends with a 0-length frame; a
    -1-length frame signals an error. generate_streaming yields one float32 waveform per generation step."""
    out = sys.stdout.buffer
    sys.stdout.flush()  # flush any pending text so it can't interleave with the binary frames
    try:
        gen = model.generate_streaming(text=text, reference_wav_path=ref_wav,
                                       cfg_value=2.0, inference_timesteps=10, retry_badcase=False)
        n = 0
        for chunk in gen:
            a = np.asarray(chunk, dtype=np.float32).squeeze()
            a = np.clip(a * STREAM_GAIN, -1.0, 1.0)
            pcm = (a * 32767.0).astype("<i2").tobytes()
            if not pcm:
                continue
            out.write(struct.pack(">i", len(pcm)))
            out.write(pcm)
            out.flush()
            n += len(pcm)
        out.write(struct.pack(">i", 0))  # clean end
        out.flush()
        log(f"[voxcpm] stream {n / 2 / 48000:.1f}s done")
    except Exception as e:  # noqa: BLE001
        import traceback
        log("[voxcpm] STREAM ERROR " + repr(e))
        log(traceback.format_exc())
        try:
            out.write(struct.pack(">i", -1))
            out.flush()
        except Exception:  # noqa: BLE001 — pipe already broken
            pass


def main():
    start_parent_watchdog()
    t0 = time.perf_counter()
    # load_denoiser=False: we only design + clone, never denoise a reference → skips the ModelScope
    # zipenhancer download and ~halves load time.
    model = VoxCPM.from_pretrained(MODEL, load_denoiser=False)
    sr = model.tts_model.sample_rate
    log(f"[voxcpm] ready in {time.perf_counter() - t0:.1f}s sr={sr}")
    print(f"SR {sr}", flush=True)  # startup handshake / load barrier for the C# host

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            req = json.loads(line)
            cmd = req.get("cmd")
            if req.get("seed") is not None:
                torch.manual_seed(int(req["seed"]))  # VoxCPM2 has no seed arg — global RNG makes it reproducible

            # stream: clone but emit binary PCM frames live (no output file, no text response line).
            if cmd == "stream":
                style = (req.get("style") or "").strip()
                body = req["text"]
                text = f"({style}){body}" if style else body
                stream_clone(model, req["refWav"], text)
                continue

            out = req["outputFile"]
            t = time.perf_counter()
            # optional quality knob: more diffusion decode steps = cleaner, more stable audio
            # (default 10 = the fast in-game setting; offline renders may ask for more)
            steps = int(req.get("timesteps") or 10)
            if cmd == "design":
                # native-tongue reference: "(voice description)<native text>" → an accented voice
                text = f"({req['desc']}){req['refText']}"
                wav = model.generate(text=text, cfg_value=2.0, inference_timesteps=steps)
            else:  # clone: speak the English line in the cloned voice, optional per-line (style) prefix
                style = (req.get("style") or "").strip()
                body = req["text"]
                text = f"({style}){body}" if style else body
                wav = model.generate(text=text, reference_wav_path=req["refWav"],
                                     cfg_value=2.0, inference_timesteps=steps)

            a = norm(wav)
            os.makedirs(os.path.dirname(out) or ".", exist_ok=True)
            sf.write(out, a, sr, subtype="PCM_16")
            log(f"[voxcpm] {cmd} {len(a) / sr:.1f}s in {time.perf_counter() - t:.2f}s -> {out}")
            print(out, flush=True)
        except Exception as e:  # noqa: BLE001 — report any failure as an empty signal line.
            import traceback
            log("[voxcpm] ERROR " + repr(e))
            log(traceback.format_exc())
            print("", flush=True)


if __name__ == "__main__":
    main()
