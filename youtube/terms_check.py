"""Audio de control de pronunciación: cada término extranjero (inglés, japonés…)
del guion, dicho por la voz clonada tal como lo va a decir en el video.

Sale en paralelo con la voz de las escenas, para escucharlo y corregir lo que
suene mal en youtube/pronunciations.json antes de dar el video por bueno.

Uso: python terms_check.py <plan.json> <ref.wav> <ref.txt> <out_dir> [modelo=1.7B]
Escribe out_dir/terminos.wav (un término tras otro, con pausa) y
out_dir/terminos.txt (la lista numerada: cómo está escrito → cómo lo lee la voz).
"""
import json
import os
import re
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from tts_shard import for_speech  # noqa: E402


def terms_in_order(plan, pron):
    """Los términos del glosario que aparecen en la narración, en orden de aparición
    y sin repetir. Si un término está dentro de otro más largo que también aparece
    ("Jujutsu" en "Jujutsu Kaisen"), va solo el largo, salvo que el corto aparezca suelto."""
    text = " ".join(s.get("narration", "") for s in plan["scenes"])
    found = []
    for term in sorted(pron, key=len, reverse=True):
        rest = text
        for longer, _ in found:
            if term in longer:
                rest = rest.replace(longer, " ")
        m = re.search(rf"(?<![\w]){re.escape(term)}(?![\w])", rest)
        if m:
            found.append((term, text.find(term)))
    return [t for t, _ in sorted(found, key=lambda x: x[1])]


def main():
    plan_path, ref_wav, ref_txt, out_dir = sys.argv[1:5]
    size = sys.argv[5] if len(sys.argv) > 5 else "1.7B"

    plan = json.load(open(plan_path, encoding="utf-8"))
    pron = {k: v for k, v in (plan.get("pronunciations") or {}).items() if not k.startswith("_") and v}
    glossary = os.path.join(os.path.dirname(os.path.abspath(__file__)), "pronunciations.json")
    if os.path.exists(glossary):
        pron.update({k: v for k, v in json.load(open(glossary, encoding="utf-8")).items() if not k.startswith("_") and v})

    # Todos los términos que marcó Gemini (aunque no cambien) + los del glosario que aparecen.
    candidates = dict.fromkeys(list(pron) + [t for t in plan.get("checkTerms") or [] if t])
    terms = terms_in_order(plan, candidates)
    os.makedirs(out_dir, exist_ok=True)
    lines = [f"{i + 1}. {t} → {for_speech(t, pron)}" for i, t in enumerate(terms)]
    open(os.path.join(out_dir, "terminos.txt"), "w", encoding="utf-8").write("\n".join(lines) + "\n")
    print("\n".join(lines) or "sin términos extranjeros", flush=True)
    if not terms:
        return

    import numpy as np
    import soundfile as sf
    import torch
    from qwen_tts import Qwen3TTSModel

    torch.set_num_threads(os.cpu_count() or 4)
    model = Qwen3TTSModel.from_pretrained(
        f"Qwen/Qwen3-TTS-12Hz-{size}-Base", device_map="cpu", dtype=torch.float32, attn_implementation="sdpa")
    prompt = model.create_voice_clone_prompt(
        ref_audio=ref_wav, ref_text=open(ref_txt, encoding="utf-8").read().strip(), x_vector_only_mode=False)

    parts, sr = [], 24000
    for i, term in enumerate(terms):
        t = time.time()
        # Igual que en el video: la voz lee la ortografía para el habla.
        wavs, sr = model.generate_voice_clone(text=for_speech(term, pron) + ".", language="Spanish",
                                              voice_clone_prompt=prompt)
        parts += [wavs[0], np.zeros(int(sr * 0.9), dtype=wavs[0].dtype)]
        print(f"{i + 1}/{len(terms)} {term}: {time.time() - t:.0f}s", flush=True)
    sf.write(os.path.join(out_dir, "terminos.wav"), np.concatenate(parts), sr)


if __name__ == "__main__":
    main()
