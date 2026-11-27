"""Voz clonada con Qwen3-TTS en una GPU gratis de Kaggle (30 h/semana).

Mismo job JSON que qwen_tts.py (lo escribe VideoRenderService):
  {"model": "1.7B", "ref": "ref.wav", "refText": "...",
   "items": [{"text": "...", "out": "scene_01.wav"}, ...]}

Pasos:
  1. Sube la referencia de voz a un dataset PRIVADO <usuario>/sheicob-voz-ref
     (la crea la primera vez; después agrega una versión).
  2. Sube kaggle_runner.py como kernel PRIVADO <usuario>/sheicob-tts con GPU T4,
     con los textos embebidos, y Kaggle lo corre.
  3. Espera a que termine y baja los WAV a la carpeta del video.

Los items cuyo WAV ya existe se saltean. Auth: KAGGLE_API_TOKEN (o ~/.kaggle).
Salida: 0 ok; 1 error; 3 sin cuota de GPU; 4 sin credenciales de Kaggle.
"""
import base64
import contextlib
import hashlib
import io
import json
import os
import shutil
import sys
import tempfile
import time

DATASET = "sheicob-voz-ref"
KERNEL = "sheicob-tts"
TIMEOUT_MIN = 55


def log(msg):
    print(f"kaggle_tts: {msg}", flush=True)


def kaggle_api():
    # El paquete imprime el aviso de auth al importarse si no hay credenciales.
    with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
        try:
            from kaggle.api.kaggle_api_extended import KaggleApi
            api = KaggleApi()
            api.authenticate()
        except BaseException as e:  # el paquete hace sys.exit sin credenciales
            return None, str(e)
    return api, None


def upload_reference(api, user, ref_path, work):
    sha = hashlib.sha256(open(ref_path, "rb").read()).hexdigest()[:12]
    ref_file = f"ref_{sha}.wav"
    folder = os.path.join(work, "dataset")
    os.makedirs(folder)
    shutil.copy(ref_path, os.path.join(folder, ref_file))
    with open(os.path.join(folder, "dataset-metadata.json"), "w") as f:
        json.dump({"title": DATASET, "id": f"{user}/{DATASET}", "licenses": [{"name": "other"}]}, f)

    try:
        api.dataset_create_version(folder, version_notes=ref_file, quiet=True, delete_old_versions=True)
        log(f"referencia subida como nueva versión ({ref_file})")
    except Exception as e:
        log(f"el dataset no existe todavía ({str(e)[:120]}) — lo creo privado")
        api.dataset_create_new(folder, public=False, quiet=True)

    for _ in range(60):
        status = str(api.dataset_status(f"{user}/{DATASET}")).lower()
        if "ready" in status:
            return ref_file
        if "error" in status:
            raise RuntimeError(f"el dataset quedó en error: {status}")
        time.sleep(5)
    raise RuntimeError("el dataset no quedó listo en 5 minutos")


def run_kernel(api, user, job, ref_file, pending, work):
    runner = open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "kaggle_runner.py"), encoding="utf-8").read()
    payload = {
        "model": job.get("model", "1.7B"),
        "refFile": ref_file,
        "refText": job["refText"],
        "items": [{"text": it["text"], "name": os.path.basename(it["out"])} for it in pending],
    }
    folder = os.path.join(work, "kernel")
    os.makedirs(folder)
    with open(os.path.join(folder, "runner.py"), "w", encoding="utf-8") as f:
        f.write(runner.replace("__JOB_B64__", base64.b64encode(json.dumps(payload).encode("utf-8")).decode("ascii")))
    with open(os.path.join(folder, "kernel-metadata.json"), "w") as f:
        json.dump({
            "id": f"{user}/{KERNEL}", "title": KERNEL, "code_file": "runner.py",
            "language": "python", "kernel_type": "script", "is_private": True,
            "enable_gpu": True, "enable_tpu": False, "enable_internet": True,
            "machine_shape": "NvidiaTeslaT4",
            "dataset_sources": [f"{user}/{DATASET}"],
            "competition_sources": [], "kernel_sources": [], "model_sources": [],
        }, f)

    resp = api.kernels_push(folder)
    err = getattr(resp, "error", None)
    if err:
        raise RuntimeError(f"kernels push: {err}")
    log(f"kernel subido ({len(pending)} escenas) — esperando la GPU…")

    start = time.time()
    last = None
    while time.time() - start < TIMEOUT_MIN * 60:
        time.sleep(20)
        r = api.kernels_status(f"{user}/{KERNEL}")
        status = str(getattr(r, "status", r)).lower()
        if status != last:
            log(f"estado: {status} ({time.time() - start:.0f}s)")
            last = status
        if "complete" in status:
            return
        if "error" in status or "cancel" in status:
            msg = getattr(r, "failure_message", "") or ""
            tail = kernel_log_tail(api, user, work)
            if "Failed to resolve" in tail or "Temporary failure in name resolution" in tail:
                # Visto el 24-nov-2026: la metadata guarda enable_internet=True pero
                # Kaggle corre el kernel SIN red si la cuenta no verificó el teléfono.
                raise RuntimeError(
                    "el kernel corrió SIN internet (no pudo instalar qwen-tts). Kaggle lo hace cuando la "
                    "cuenta no tiene el teléfono verificado: kaggle.com → Settings → Phone Verification.")
            raise RuntimeError(f"el kernel terminó en {status}: {msg}\n{tail}\nLog: https://www.kaggle.com/code/{user}/{KERNEL}")
    raise RuntimeError(f"el kernel no terminó en {TIMEOUT_MIN} minutos")


def kernel_log_tail(api, user, work, lines=15):
    """Últimas líneas del log del kernel (Kaggle lo guarda como JSON de streams)."""
    folder = os.path.join(work, "errlog")
    try:
        os.makedirs(folder, exist_ok=True)
        api.kernels_output(f"{user}/{KERNEL}", path=folder, force=True, quiet=True)
        raw = open(os.path.join(folder, f"{KERNEL}.log"), encoding="utf-8").read()
        try:
            text = "".join(e.get("data", "") for e in json.loads(raw))
        except ValueError:
            text = raw
        return "\n".join([l for l in text.splitlines() if l.strip()][-lines:])
    except Exception as e:
        return f"(no se pudo bajar el log: {e})"


def main() -> int:
    with open(sys.argv[1], encoding="utf-8") as f:
        job = json.load(f)
    pending = [it for it in job["items"] if not os.path.exists(it["out"])]
    log(f"{len(job['items']) - len(pending)} en caché, {len(pending)} por generar")
    if not pending:
        return 0

    api, auth_error = kaggle_api()
    if api is None:
        print(f"kaggle_tts: sin credenciales de Kaggle (KAGGLE_API_TOKEN): {auth_error[:200]}", file=sys.stderr)
        return 4
    user = api.get_config_value("username")

    work = tempfile.mkdtemp(prefix="kaggle_tts_")
    try:
        ref_file = upload_reference(api, user, job["ref"], work)
        run_kernel(api, user, job, ref_file, pending, work)

        out = os.path.join(work, "output")
        os.makedirs(out)
        api.kernels_output(f"{user}/{KERNEL}", path=out, force=True, quiet=True)
        missing = []
        for it in pending:
            got = os.path.join(out, os.path.basename(it["out"]))
            if os.path.exists(got):
                shutil.copy(got, it["out"])
            else:
                missing.append(os.path.basename(it["out"]))
        if missing:
            raise RuntimeError(f"el kernel no devolvió {missing}")
        log(f"{len(pending)} escenas descargadas")
        return 0
    except Exception as e:
        msg = str(e)
        print(f"kaggle_tts: {msg[:500]}", file=sys.stderr)
        return 3 if "quota" in msg.lower() else 1
    finally:
        shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
