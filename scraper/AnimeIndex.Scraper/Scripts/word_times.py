"""Tiempos reales de cada palabra de la voz, para cambiar la toma cuando se DICE algo.

La voz clonada no da tiempos por palabra. faster-whisper (CPU, modelo "base":
"small" tardaba ~25 s por escena en una máquina cargada y los tiempos de "base"
alcanzan para cortar tomas)
transcribe cada escena con timestamps por palabra; el render alinea esas palabras
con la narración que ya conoce (no usa el texto de whisper, solo sus tiempos).

Uso: python word_times.py <job.json> <salida.json>
  job:    {"files": ["voice_01.wav", ...], "language": "es", "model": "base"}
  salida: {"voice_01.wav": [["palabra", inicio_s, fin_s], ...], ...}

Las escenas ya procesadas se guardan junto al audio (<audio>.<sha1>.words.json,
por CONTENIDO: el render reescribe los audios en cada corrida) y se reutilizan.
"""
import hashlib
import json
import os
import sys

from faster_whisper import WhisperModel


def main():
    job = json.load(open(sys.argv[1], encoding="utf-8"))
    files = job.get("files", [])
    language = job.get("language", "es")
    out = {}
    model = None
    for path in files:
        digest = hashlib.sha1(open(path, "rb").read()).hexdigest()[:12]
        cache = f"{path}.{digest}.words.json"
        if os.path.exists(cache):
            out[path] = json.load(open(cache, encoding="utf-8"))
            continue
        if model is None:
            model = WhisperModel(job.get("model", "base"), device="cpu", compute_type="int8",
                                 cpu_threads=os.cpu_count() or 4)
        segments, _ = model.transcribe(path, language=language, word_timestamps=True,
                                       beam_size=1, vad_filter=False)
        words = [[w.word.strip(), round(w.start, 3), round(w.end, 3)]
                 for seg in segments for w in (seg.words or []) if w.word.strip()]
        json.dump(words, open(cache, "w", encoding="utf-8"), ensure_ascii=False)
        out[path] = words
    json.dump(out, open(sys.argv[2], "w", encoding="utf-8"), ensure_ascii=False)


if __name__ == "__main__":
    main()
