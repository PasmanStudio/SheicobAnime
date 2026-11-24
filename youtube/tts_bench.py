"""Benchmark: Qwen3-TTS clonando la voz del canal en CPU (runner de GitHub Actions).

Mide cuánto tarda la CPU de un runner gratis (4 vCPU, 16 GB) en generar audio
con el modelo elegido, para saber si se puede producir sin GPU y en cuántos
runners en paralelo habría que repartir las escenas de un video de 10 minutos.

Uso: python tts_bench.py <ref.wav> <ref.txt> <out_dir> [modelo=1.7B]
Escribe los WAV en out_dir y una tabla markdown en $GITHUB_STEP_SUMMARY.
"""
import os
import sys
import time

import soundfile as sf
import torch
from qwen_tts import Qwen3TTSModel

SCENES = [
    "¿Por qué Frieren casi nunca muestra todo su poder? Uno de los detalles más interesantes de la serie es que ella oculta su maná todo el tiempo.",
    "De hecho, lleva más de mil años suprimiéndolo, y eso tiene una razón muy clara.",
    "En primer lugar, su maestra Flamme le enseñó que los demonios juzgan a sus rivales por la cantidad de maná que muestran.",
]

ref_wav, ref_txt, out_dir = sys.argv[1:4]
size = sys.argv[4] if len(sys.argv) > 4 else "1.7B"
os.makedirs(out_dir, exist_ok=True)
torch.set_num_threads(os.cpu_count() or 4)

rows = []
t0 = time.time()
model = Qwen3TTSModel.from_pretrained(
    f"Qwen/Qwen3-TTS-12Hz-{size}-Base", device_map="cpu", dtype=torch.float32, attn_implementation="sdpa")
load = time.time() - t0
print(f"modelo {size} cargado en {load:.0f}s ({os.cpu_count()} CPUs, torch {torch.__version__})", flush=True)

t = time.time()
prompt = model.create_voice_clone_prompt(
    ref_audio=ref_wav, ref_text=open(ref_txt, encoding="utf-8").read().strip(), x_vector_only_mode=False)
print(f"molde de voz en {time.time() - t:.1f}s", flush=True)

total_audio = total_gen = 0.0
for i, text in enumerate(SCENES, 1):
    t = time.time()
    wavs, sr = model.generate_voice_clone(text=text, language="Spanish", voice_clone_prompt=prompt)
    gen = time.time() - t
    audio = len(wavs[0]) / sr
    sf.write(os.path.join(out_dir, f"escena_{i}.wav"), wavs[0], sr)
    total_audio += audio
    total_gen += gen
    rows.append((i, audio, gen, gen / audio))
    print(f"escena {i}: {audio:.1f}s de audio en {gen:.0f}s (x{gen / audio:.1f})", flush=True)

rtf = total_gen / total_audio
ten_min = rtf * 600 / 60
summary = [
    f"## Qwen3-TTS {size} en CPU del runner",
    "",
    f"- CPUs: {os.cpu_count()} · torch {torch.__version__} · carga del modelo: {load:.0f}s",
    "",
    "| Escena | Audio (s) | Generación (s) | s por s de audio |",
    "|---|---|---|---|",
    *[f"| {i} | {a:.1f} | {g:.0f} | {r:.1f} |" for i, a, g, r in rows],
    f"| **Total** | **{total_audio:.1f}** | **{total_gen:.0f}** | **{rtf:.1f}** |",
    "",
    f"**Proyección para 10 min de audio:** {ten_min:.0f} min en 1 runner · "
    f"{ten_min / 4:.0f} min con 4 · {ten_min / 8:.0f} min con 8 · {ten_min / 16:.0f} min con 16 (+ carga del modelo en cada uno).",
]
print("\n".join(summary))
if os.environ.get("GITHUB_STEP_SUMMARY"):
    with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as f:
        f.write("\n".join(summary) + "\n")
