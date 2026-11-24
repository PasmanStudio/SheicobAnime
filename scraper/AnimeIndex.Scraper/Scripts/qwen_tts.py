"""Voz clonada con Qwen3-TTS para el prototipo --youtube.

Lee un job JSON (lo escribe VideoRenderService):
  {"space": "Qwen/Qwen3-TTS", "model": "1.7B", "ref": "ref.wav", "refText": "...",
   "items": [{"text": "...", "out": "scene_01.wav"}, ...]}

Genera un WAV por item y SALTEA los que ya existen: si se acaba la cuota de
GPU a mitad de camino, la siguiente corrida sigue desde donde quedó.

Backend hoy: la Space pública de Qwen en Hugging Face (ZeroGPU), con HF_TOKEN.
Salida: exit 0 = todo listo; exit 3 = sin cuota de GPU (reintentar más tarde).
"""
import json
import os
import shutil
import sys

from gradio_client import Client, handle_file


def main() -> int:
    with open(sys.argv[1], encoding="utf-8") as f:
        job = json.load(f)

    pending = [it for it in job["items"] if not os.path.exists(it["out"])]
    print(f"qwen_tts: {len(job['items']) - len(pending)} en caché, {len(pending)} por generar", flush=True)
    if not pending:
        return 0

    client = Client(job.get("space", "Qwen/Qwen3-TTS"), token=os.environ.get("HF_TOKEN"), verbose=False)
    ref = handle_file(job["ref"])
    for i, item in enumerate(pending, 1):
        try:
            wav, status = client.predict(
                ref, job["refText"], item["text"], "Spanish", False, job.get("model", "1.7B"),
                api_name="/generate_voice_clone")
        except Exception as e:  # gradio_client.exceptions.AppError y errores de red
            msg = str(e)
            print(f"qwen_tts: falló {os.path.basename(item['out'])}: {msg[:300]}", file=sys.stderr, flush=True)
            return 3 if "quota" in msg.lower() else 1
        shutil.copy(wav, item["out"])
        print(f"qwen_tts: {i}/{len(pending)} {os.path.basename(item['out'])} ({status})", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
