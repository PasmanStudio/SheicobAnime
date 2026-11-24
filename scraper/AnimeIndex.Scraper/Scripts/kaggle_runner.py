# Notebook de Kaggle (GPU T4) que genera la voz clonada del prototipo --youtube.
# NO se corre localmente: kaggle_tts.py le inyecta el job (base64) y lo sube como
# kernel privado. La referencia de voz llega por un dataset privado.
import base64
import glob
import json
import subprocess
import sys
import time

JOB = json.loads(base64.b64decode("__JOB_B64__").decode("utf-8"))
t0 = time.time()

subprocess.run([sys.executable, "-m", "pip", "install", "-q", "qwen-tts"], check=True)

import soundfile as sf  # noqa: E402
import torch  # noqa: E402
from qwen_tts import Qwen3TTSModel  # noqa: E402

refs = glob.glob(f"/kaggle/input/**/{JOB['refFile']}", recursive=True)
if not refs:
    raise SystemExit(f"No está {JOB['refFile']} en /kaggle/input: {glob.glob('/kaggle/input/**', recursive=True)[:20]}")

# La T4 (capability 7.5) no tiene bfloat16 nativo y en float16 el modelo puede
# dar NaN: float32 entra holgado en sus 16 GB (1.7B ≈ 7 GB).
major, _ = torch.cuda.get_device_capability(0)
dtype = torch.bfloat16 if major >= 8 else torch.float32
print(f"GPU {torch.cuda.get_device_name(0)}, dtype {dtype}, setup {time.time() - t0:.0f}s", flush=True)

model = Qwen3TTSModel.from_pretrained(
    f"Qwen/Qwen3-TTS-12Hz-{JOB['model']}-Base",
    device_map="cuda:0",
    dtype=dtype,
    attn_implementation="sdpa",
)
# El "molde" de la voz se calcula una vez y se reutiliza en todas las escenas.
prompt = model.create_voice_clone_prompt(ref_audio=refs[0], ref_text=JOB["refText"], x_vector_only_mode=False)

for i, item in enumerate(JOB["items"], 1):
    t = time.time()
    wavs, sr = model.generate_voice_clone(text=item["text"], language="Spanish", voice_clone_prompt=prompt)
    sf.write(f"/kaggle/working/{item['name']}", wavs[0], sr)
    print(f"{i}/{len(JOB['items'])} {item['name']}: {len(wavs[0]) / sr:.1f}s de audio en {time.time() - t:.1f}s", flush=True)

print(f"Listo en {time.time() - t0:.0f}s", flush=True)
