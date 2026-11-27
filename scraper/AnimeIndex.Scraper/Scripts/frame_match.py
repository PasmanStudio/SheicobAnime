"""Elige qué fotograma de los videos oficiales muestra lo que dice cada toma.

Usa CLIP (modelo abierto texto↔imagen, corre en CPU): compara cada descripción
visual del storyboard ("a man and a woman embracing under neon lights") con cada
fotograma de los videos fuente (tráiler, PV, el videoclip del reel) y devuelve
los más parecidos. Así un clip deja de ser al azar y acompaña la frase.

Uso: python frame_match.py <job.json> <salida.json>
  job:    {"frames": ["ruta1.jpg", ...], "queries": ["descripción", ...]}
  salida: {"descripción": [[índice_fotograma, puntaje], ...]}  (los 10 mejores, de mayor a menor)

El puntaje es la similitud coseno de CLIP (~0,15 = nada que ver, ~0,30 = muy parecido).
"""
import json
import sys

import open_clip
import torch
from PIL import Image

MODEL, PRETRAINED = "ViT-B-32", "laion2b_s34b_b79k"


def main():
    job = json.load(open(sys.argv[1], encoding="utf-8"))
    frames, queries = job["frames"], job["queries"]
    if not frames or not queries:
        json.dump({}, open(sys.argv[2], "w", encoding="utf-8"))
        return

    torch.set_num_threads(max(1, torch.get_num_threads()))
    model, _, preprocess = open_clip.create_model_and_transforms(MODEL, pretrained=PRETRAINED)
    tokenizer = open_clip.get_tokenizer(MODEL)
    model.eval()

    with torch.no_grad():
        images = []
        for start in range(0, len(frames), 32):
            batch = torch.stack([preprocess(Image.open(p).convert("RGB")) for p in frames[start:start + 32]])
            images.append(model.encode_image(batch))
        img = torch.cat(images)
        img /= img.norm(dim=-1, keepdim=True)

        # Plantilla de anime: CLIP compara mejor contra frases de estilo foto/ilustración.
        txt = model.encode_text(tokenizer([f"an anime scene of {q}" for q in queries]))
        txt /= txt.norm(dim=-1, keepdim=True)
        sims = (txt @ img.T).tolist()

    out = {}
    for q, row in zip(queries, sims):
        ranked = sorted(enumerate(row), key=lambda x: -x[1])[:10]
        out[q] = [[i, round(s, 4)] for i, s in ranked]
    json.dump(out, open(sys.argv[2], "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    print(f"frame_match: {len(queries)} descripciones × {len(frames)} fotogramas", flush=True)


if __name__ == "__main__":
    main()
