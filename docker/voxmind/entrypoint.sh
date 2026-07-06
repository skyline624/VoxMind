#!/usr/bin/env bash
# Entrypoint de l'image VoxMind tout-en-un : prépare les dossiers de données puis lance le superviseur
# (sidecar vLLM-omni + VoxMind.Api).
set -euo pipefail

MODELS_DIR="${VOXMIND_MODELS_DIR:-/app/models}"
DATA_DIR="${VOXMIND_DATA_DIR:-/app/voice_data}"
CFG_DIR="${DATA_DIR}/config"

mkdir -p "${MODELS_DIR}" "${CFG_DIR}" "${DATA_DIR}/sessions" "${DATA_DIR}/logs" "${DATA_DIR}/profiles"

# Dépose la config par défaut (moteur TTS « qwen3 » → sidecar vLLM) si le volume n'en fournit pas.
if [ ! -f "${CFG_DIR}/config.json" ]; then
    cp /app/config.default.json "${CFG_DIR}/config.json"
    echo "[entrypoint] config par défaut déposée : ${CFG_DIR}/config.json"
fi

BACKEND="${TTS_BACKEND:-qwen3}"

# Aligne la config VoxMind (ml.tts.qwen3_vllm) sur le backend actif : le champ `model` DOIT correspondre au
# modèle réellement servi par le sidecar (sinon vLLM renvoie « model not found »). On préserve le reste
# (base_url, langues, reference_* de clonage) — inutile en Voxtral (task_type vide) mais sans effet.
python3 - "${CFG_DIR}/config.json" <<'PYEOF'
import json, os, sys
p = sys.argv[1]
try:
    d = json.load(open(p, encoding="utf-8"))
except Exception:
    d = {}
q = d.setdefault("ml", {}).setdefault("tts", {}).setdefault("qwen3_vllm", {})
backend = os.environ.get("TTS_BACKEND", "qwen3")
if backend == "voxtral":
    q["model"] = os.environ.get("VOXTRAL_MODEL", "mistralai/Voxtral-4B-TTS-2603")
    q["task_type"] = ""                                          # Voxtral : voix = preset, pas de task_type
    q["default_voice"] = os.environ.get("VOXTRAL_VOICE", "fr_female")
else:
    q["model"] = os.environ.get("QWEN3_MODEL", q.get("model", "Qwen/Qwen3-TTS-12Hz-1.7B-CustomVoice"))
    q["task_type"] = os.environ.get("QWEN3_TASK_TYPE", q.get("task_type", "CustomVoice"))  # Base=clonage
q["enabled"] = True
json.dump(d, open(p, "w", encoding="utf-8"), indent=2, ensure_ascii=False)
print(f"[entrypoint] backend={backend} → qwen3_vllm.model={q['model']} task_type={q.get('task_type')!r} voice={q.get('default_voice')!r}")
PYEOF

# Deploy-config vLLM ajusté (qwen3 uniquement) : le pipeline Omni réserve la VRAM PAR STAGE. On abaisse ces
# valeurs (défaut 0.2) via QWEN3_STAGE_GPU_UTIL. Voxtral utilise son propre voxtral_tts.yaml d'upstream.
if [ "$BACKEND" != "voxtral" ]; then
    DEPLOY_SRC=/app/vllm-omni/vllm_omni/deploy/qwen3_tts.yaml
    DEPLOY_RUNTIME=/app/qwen3_tts.runtime.yaml
    STAGE_UTIL="${QWEN3_STAGE_GPU_UTIL:-0.2}"
    sed "s/gpu_memory_utilization:[[:space:]]*[0-9.]\+/gpu_memory_utilization: ${STAGE_UTIL}/g" "$DEPLOY_SRC" > "$DEPLOY_RUNTIME"
    echo "[entrypoint] deploy-config qwen3 : gpu_memory_utilization par stage = ${STAGE_UTIL}"
fi

echo "[entrypoint] backend=${BACKEND}  data=${DATA_DIR}  vLLM=127.0.0.1:${VLLM_PORT:-8091}"
echo "[entrypoint] note : au 1er démarrage/switch, le sidecar charge le modèle (~3-5 min) ; /v1/audio/speech"
echo "[entrypoint]        model=qwen3 répond 503 pendant ce temps, Kokoro reste dispo."

exec supervisord -c /etc/supervisor/conf.d/voxmind.conf
