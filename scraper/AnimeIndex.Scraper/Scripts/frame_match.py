"""Elige qué fotograma de los videos oficiales muestra lo que dice cada toma.

Usa CLIP (modelo abierto texto↔imagen, corre en CPU): compara cada descripción
visual del storyboard ("a man and a woman embracing under neon lights") con cada
fotograma de los videos fuente (tráiler, PV, el videoclip del reel) y devuelve
los más parecidos. Así un clip deja de ser al azar y acompaña la frase.

Uso: python frame_match.py <job.json> <salida.json>
  job:    {"frames": ["ruta1.jpg", ...], "queries": ["descripción", ...], "top": 10,
           "faces": {"David Martinez": "retrato.png", ...}}
  salida: {"descripción": [[índice_fotograma, puntaje], ...],   (los "top" mejores, de mayor a menor)
           "face:David Martinez": [[índice_fotograma, margen], ...]}

"top" es opcional (10). Con varias fuentes de metraje se piden todos: cada toma
se queda después con los fotogramas de la fuente de su escena.

El puntaje es la similitud coseno de CLIP (~0,15 = nada que ver, ~0,30 = muy parecido).

"faces" (opcional): retratos oficiales de los personajes nombrados en el video. Para
cada uno, los planos donde aparece: CLIP imagen↔imagen sobre tres recortes cuadrados
del fotograma (izquierda, centro, derecha; el personaje rara vez está centrado) y,
como puntaje, el MARGEN contra el mejor de los otros retratos: cuánto más se parece
a este personaje que a cualquier otro del elenco. Medido el 27-nov-2026 con el
tráiler de la T1 de Cyberpunk: Edgerunners (106 planos etiquetados a mano): el
parecido absoluto no separa (un logo o un plano de otro personaje puntúan igual que
uno del buscado); el margen > 0 sí, con pocos aciertos pero casi sin errores. Con
un solo retrato no hay elenco: margen = parecido − 0,70.
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
    top = max(1, int(job.get("top", 10)))
    if not frames or not (queries or job.get("faces")):
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

        sims = []
        if queries:
            # Plantilla de anime: CLIP compara mejor contra frases de estilo foto/ilustración.
            txt = model.encode_text(tokenizer([f"an anime scene of {q}" for q in queries]))
            txt /= txt.norm(dim=-1, keepdim=True)
            sims = (txt @ img.T).tolist()

        faces = job.get("faces") or {}
        names = list(faces)
        face_scores = {}
        if names:
            crops = []
            for p in frames:
                im = Image.open(p).convert("RGB")
                w, h = im.size
                side = min(w, h)
                for x in (0, (w - side) // 2, w - side):
                    crops.append(preprocess(im.crop((x, (h - side) // 2, x + side, (h - side) // 2 + side))))
            enc = torch.cat([model.encode_image(torch.stack(crops[s:s + 48])) for s in range(0, len(crops), 48)])
            enc = (enc / enc.norm(dim=-1, keepdim=True)).view(len(frames), 3, -1)
            ref = model.encode_image(torch.stack([preprocess(Image.open(faces[n]).convert("RGB")) for n in names]))
            ref /= ref.norm(dim=-1, keepdim=True)
            sim = (enc @ ref.T).max(dim=1).values  # [fotogramas, retratos]: el mejor de los tres recortes
            for j, n in enumerate(names):
                if len(names) == 1:
                    margin = sim[:, j] - 0.70
                else:
                    others = torch.cat([sim[:, :j], sim[:, j + 1:]], dim=1).max(dim=1).values
                    margin = sim[:, j] - others
                face_scores[n] = margin.tolist()

    out = {}
    for n, row in face_scores.items():
        ranked = sorted(enumerate(row), key=lambda x: -x[1])[:top]
        out["face:" + n] = [[i, round(s, 4)] for i, s in ranked]
    for q, row in zip(queries, sims):
        ranked = sorted(enumerate(row), key=lambda x: -x[1])[:top]
        out[q] = [[i, round(s, 4)] for i, s in ranked]
    json.dump(out, open(sys.argv[2], "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    print(f"frame_match: {len(queries)} descripciones y {len(face_scores)} retratos × {len(frames)} fotogramas", flush=True)


if __name__ == "__main__":
    main()
