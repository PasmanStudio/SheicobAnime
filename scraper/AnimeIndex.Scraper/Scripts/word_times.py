"""Segundo exacto en que la voz dice cada palabra, para cambiar la toma cuando se NOMBRA algo.

La voz clonada no da tiempos por palabra, pero el texto que leyó se conoce: es la
narración con el glosario de pronunciación aplicado. En vez de transcribir (whisper
oye "Edgar Onnors" por "Edgerunners" y sus tiempos derivan), se hace ALINEACIÓN
FORZADA: un modelo wav2vec2 en español (CTC) da, cuadro a cuadro de 20 ms, la
probabilidad de cada letra, y Viterbi encuentra el camino que recorre el texto
conocido letra por letra. El inicio de cada palabra es el cuadro de su primera letra.

Modelo: jonatasgrosman/wav2vec2-large-xlsr-53-spanish (Apache-2.0, ~1.2 GB; en CI
queda en la caché de Hugging Face). Solo CPU.

Uso: python word_times.py <job.json> <salida.json>
  job:    {"files": ["voice_01.wav", ...], "texts": ["texto que leyó la voz", ...]}
  salida: {"voice_01.wav": [["palabra", inicio_s, fin_s], ...], ...}
          y en stderr, por escena, la confianza media del alineado.

Las escenas ya procesadas se guardan junto al audio (<audio>.<sha1>.align.json, por
CONTENIDO del audio + texto + modelo) y se reutilizan.
"""
import hashlib
import json
import os
import subprocess
import sys
import unicodedata

import numpy as np
import torch
from transformers import Wav2Vec2ForCTC, Wav2Vec2Processor

MODEL = "jonatasgrosman/wav2vec2-large-xlsr-53-spanish"
RATE = 16000
# Debajo de esto (log-prob media por letra) el alineado es dudoso: la voz se comió
# o repitió palabras. Se avisa; el render igual usa los tiempos.
LOW_CONFIDENCE = -1.5


def load_audio(path):
    """Audio → float32 mono 16 kHz (ffmpeg ya está en el runner del render)."""
    raw = subprocess.run(
        ["ffmpeg", "-v", "error", "-i", path, "-ac", "1", "-ar", str(RATE), "-f", "f32le", "-"],
        check=True, capture_output=True).stdout
    return np.frombuffer(raw, dtype=np.float32)


def normalize_word(word, vocab):
    """Palabra → letras que el modelo conoce (minúsculas; á é í ó ú ñ ü se quedan,
    otras tildes se quitan; dígitos y signos no se pueden alinear y se descartan)."""
    out = []
    for c in word.lower():
        if c in vocab:
            out.append(c)
            continue
        base = "".join(b for b in unicodedata.normalize("NFD", c) if unicodedata.category(b) != "Mn")
        out.extend(b for b in base if b in vocab)
    return "".join(out).strip("'-")


def viterbi(emission, tokens, blank):
    """Camino CTC más probable que recorre `tokens` en orden (puede quedarse en un
    token o pasar por blancos). Devuelve, por token, (primer_cuadro, último_cuadro, logprob)."""
    T, N = emission.shape[0], len(tokens)
    if T < N:
        return None
    # trellis[t, j] = mejor puntaje habiendo emitido los primeros j tokens en t cuadros.
    trellis = np.full((T + 1, N + 1), -np.inf, dtype=np.float64)
    trellis[0, 0] = 0.0
    trellis[1:, 0] = np.cumsum(emission[:, blank])
    tok = np.asarray(tokens)
    for t in range(T):
        stay = trellis[t, 1:] + emission[t, blank]
        move = trellis[t, :-1] + emission[t, tok]
        trellis[t + 1, 1:] = np.maximum(stay, move)
    if not np.isfinite(trellis[T, N]):
        return None
    # Hacia atrás: en cada cuadro, ¿se emitió el token j o fue un blanco?
    spans = [[None, None, 0.0] for _ in range(N)]
    j = N
    for t in range(T, 0, -1):
        if j == 0:
            break
        stay = trellis[t - 1, j] + emission[t - 1, blank]
        move = trellis[t - 1, j - 1] + emission[t - 1, tokens[j - 1]]
        if move >= stay:
            s = spans[j - 1]
            s[0] = t - 1
            if s[1] is None:
                s[1] = t - 1
            s[2] += emission[t - 1, tokens[j - 1]]
            j -= 1
    return spans


def align(model, processor, audio, text):
    vocab = processor.tokenizer.get_vocab()
    blank = processor.tokenizer.pad_token_id
    sep = vocab["|"]
    words = text.split()
    tokens, owner = [], []
    for i, w in enumerate(words):
        letters = normalize_word(w, vocab)
        if not letters:
            continue
        if tokens:
            tokens.append(sep)
            owner.append(-1)
        for c in letters:
            tokens.append(vocab[c])
            owner.append(i)
    if not tokens:
        return [], 0.0

    inputs = processor(audio, sampling_rate=RATE, return_tensors="pt")
    with torch.inference_mode():
        logits = model(inputs.input_values).logits[0]
    emission = torch.log_softmax(logits, dim=-1).numpy()
    seconds_per_frame = len(audio) / RATE / emission.shape[0]

    spans = viterbi(emission, tokens, blank)
    if spans is None:
        return [], float("-inf")

    result, scores = [], []
    for i, w in enumerate(words):
        mine = [spans[k] for k in range(len(tokens)) if owner[k] == i and spans[k][0] is not None]
        if not mine:
            continue
        start = min(s[0] for s in mine) * seconds_per_frame
        end = (max(s[1] for s in mine) + 1) * seconds_per_frame
        scores.extend(s[2] for s in mine)
        result.append([w, round(start, 3), round(end, 3)])
    return result, float(np.mean(scores)) if scores else 0.0


def main():
    job = json.load(open(sys.argv[1], encoding="utf-8"))
    files = job.get("files", [])
    texts = job.get("texts", [])
    if len(texts) != len(files):
        sys.exit(f"word_times: {len(files)} audios y {len(texts)} textos")

    torch.set_num_threads(os.cpu_count() or 4)
    model = processor = None
    out = {}
    low = 0
    for path, text in zip(files, texts):
        digest = hashlib.sha1(open(path, "rb").read() + text.encode("utf-8") + MODEL.encode()).hexdigest()[:12]
        cache = f"{path}.{digest}.align.json"
        if os.path.exists(cache):
            out[path] = json.load(open(cache, encoding="utf-8"))
            continue
        if model is None:
            processor = Wav2Vec2Processor.from_pretrained(MODEL)
            model = Wav2Vec2ForCTC.from_pretrained(MODEL).eval()
        words, confidence = align(model, processor, load_audio(path), text)
        flag = ""
        if confidence < LOW_CONFIDENCE:
            low += 1
            flag = "  <- DUDOSO"
        print(f"{os.path.basename(path)}: {len(words)}/{len(text.split())} palabras, "
              f"confianza {confidence:.2f}{flag}", file=sys.stderr, flush=True)
        json.dump(words, open(cache, "w", encoding="utf-8"), ensure_ascii=False)
        out[path] = words
    if low:
        print(f"word_times: {low} escena(s) con alineado dudoso", file=sys.stderr, flush=True)
    json.dump(out, open(sys.argv[2], "w", encoding="utf-8"), ensure_ascii=False)


if __name__ == "__main__":
    main()
