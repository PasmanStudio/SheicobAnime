"""Genera la voz clonada (Qwen3-TTS) de UNA PARTE de las escenas de un plan.

El workflow youtube-video.yml lo corre en N runners en paralelo: cada uno toma
su parte, así un video de 10 minutos sale en ~15 min en vez de ~2 h (benchmark
del 24-nov-2026 en un runner de 4 CPU: 12,3 s de cómputo por segundo de audio).

Uso: python tts_shard.py <plan.json> <ref.wav> <ref.txt> <out_dir> <shard> <shards> [modelo=1.7B]
Escribe out_dir/scene_NN.wav (NN = número de escena en el plan, desde 01).
"""
import json
import re
import os
import sys
import time


def assign(lengths, shards):
    """Reparte escenas entre runners equilibrando letras (lo que cuesta generar):
    la escena más larga va al runner con menos carga. Determinístico: todos los
    runners calculan el mismo reparto sin coordinarse."""
    load = [0] * shards
    owner = {}
    for i in sorted(range(len(lengths)), key=lambda k: (-lengths[k], k)):
        target = min(range(shards), key=lambda s: (load[s], s))
        owner[i] = target
        load[target] += lengths[i]
    return owner


def for_speech(text, pron):
    """Igual que VideoPlan.ForSpeech en C#: palabra completa, términos largos primero."""
    for term in sorted(pron, key=len, reverse=True):
        text = re.sub(rf"(?<![\w]){re.escape(term)}(?![\w])", pron[term], text)
    return text


def main():
    plan_path, ref_wav, ref_txt, out_dir = sys.argv[1:5]
    shard, shards = int(sys.argv[5]), int(sys.argv[6])
    size = sys.argv[7] if len(sys.argv) > 7 else "1.7B"

    plan = json.load(open(plan_path, encoding="utf-8"))
    pron = {k: v for k, v in (plan.get("pronunciations") or {}).items() if not k.startswith("_") and v}
    glossary = os.path.join(os.path.dirname(os.path.abspath(__file__)), "pronunciations.json")
    if os.path.exists(glossary):  # el glosario del repo pisa lo que sugirió Gemini
        pron.update({k: v for k, v in json.load(open(glossary, encoding="utf-8")).items() if not k.startswith("_") and v})
    texts = [for_speech(s["narration"].strip(), pron) for s in plan["scenes"] if s.get("narration", "").strip()]
    owner = assign([len(t) for t in texts], shards)
    mine = [i for i in range(len(texts)) if owner[i] == shard]
    print(f"runner {shard + 1}/{shards}: escenas {[i + 1 for i in mine]} "
          f"({sum(len(texts[i]) for i in mine)} letras)", flush=True)
    if not mine:
        return

    import soundfile as sf
    import torch
    from qwen_tts import Qwen3TTSModel

    torch.set_num_threads(os.cpu_count() or 4)
    os.makedirs(out_dir, exist_ok=True)
    t0 = time.time()
    model = Qwen3TTSModel.from_pretrained(
        f"Qwen/Qwen3-TTS-12Hz-{size}-Base", device_map="cpu", dtype=torch.float32, attn_implementation="sdpa")
    prompt = model.create_voice_clone_prompt(
        ref_audio=ref_wav, ref_text=open(ref_txt, encoding="utf-8").read().strip(), x_vector_only_mode=False)
    print(f"modelo {size} + molde de voz listos en {time.time() - t0:.0f}s", flush=True)

    for i in mine:
        t = time.time()
        wavs, sr = model.generate_voice_clone(text=texts[i], language="Spanish", voice_clone_prompt=prompt)
        sf.write(os.path.join(out_dir, f"scene_{i + 1:02d}.wav"), wavs[0], sr)
        audio = len(wavs[0]) / sr
        print(f"escena {i + 1}: {audio:.1f}s de audio en {time.time() - t:.0f}s", flush=True)


if __name__ == "__main__":
    main()
