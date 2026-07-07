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

# Aligne les DEUX profils VoxMind (ml.tts.qwen3_vllm + voxtral_vllm) sur les modèles réellement servis : le
# champ `model` de chacun DOIT matcher le modèle du sidecar quand ce backend est actif (sinon « model not
# found »). On ne patche que model/task_type/voice ; on préserve le reste (langues, reference_* de clonage).
python3 - "${CFG_DIR}/config.json" <<'PYEOF'
import json, os, sys
p = sys.argv[1]
try:
    d = json.load(open(p, encoding="utf-8"))
except Exception:
    d = {}
tts = d.setdefault("ml", {}).setdefault("tts", {})
q = tts.setdefault("qwen3_vllm", {})
q["model"] = os.environ.get("QWEN3_MODEL", q.get("model", "Qwen/Qwen3-TTS-12Hz-1.7B-CustomVoice"))
q["task_type"] = os.environ.get("QWEN3_TASK_TYPE", q.get("task_type", "CustomVoice"))   # Base = clonage
q["enabled"] = True
v = tts.setdefault("voxtral_vllm", {})
v["model"] = os.environ.get("VOXTRAL_MODEL", "mistralai/Voxtral-4B-TTS-2603")
v["task_type"] = ""                                                                     # Voxtral : voix = preset
v["default_voice"] = os.environ.get("VOXTRAL_VOICE", "fr_female")
v.setdefault("base_url", q.get("base_url", "http://127.0.0.1:8091"))
v["enabled"] = True
json.dump(d, open(p, "w", encoding="utf-8"), indent=2, ensure_ascii=False)
print(f"[entrypoint] profils : qwen3={q['model']} (task={q['task_type']!r}) | voxtral={v['model']} (voix={v['default_voice']!r})")
PYEOF

# Fichier d'état du backend actif : source de vérité partagée par serve-tts.sh (sidecar), le watcher et
# VoxMind. TOUJOURS (ré)initialisé à TTS_BACKEND au démarrage → l'env (docker run -e / switch-tts.ps1) est le
# backend DURABLE ; une bascule via l'API (model:voxtral) est TEMPORAIRE (revient à TTS_BACKEND au restart).
STATE_FILE="${CFG_DIR}/tts_backend"
echo -n "${BACKEND}" > "$STATE_FILE"
echo "[entrypoint] backend actif = ${BACKEND}  (fichier ${STATE_FILE})"

# Deploy-config qwen3 ajusté (VRAM par stage) — toujours généré (utilisé quand le backend actif est qwen3).
DEPLOY_SRC=/app/vllm-omni/vllm_omni/deploy/qwen3_tts.yaml
DEPLOY_RUNTIME=/app/qwen3_tts.runtime.yaml
STAGE_UTIL="${QWEN3_STAGE_GPU_UTIL:-0.2}"
sed "s/gpu_memory_utilization:[[:space:]]*[0-9.]\+/gpu_memory_utilization: ${STAGE_UTIL}/g" "$DEPLOY_SRC" > "$DEPLOY_RUNTIME"

# Deploy-config Voxtral ajusté : le stage0 réserve 0.8 du GPU (~19 Go, surtout du pool KV spéculatif inutile
# en single-stream). On abaisse ce 0.8 → ~0.45 (≈ 11-13 Go) via VOXTRAL_STAGE_GPU_UTIL ; stage1 (0.1) inchangé.
VOX_SRC=/app/vllm-omni/vllm_omni/deploy/voxtral_tts.yaml
VOX_RUNTIME=/app/voxtral_tts.runtime.yaml
if [ -f "$VOX_SRC" ]; then
    VOX_UTIL="${VOXTRAL_STAGE_GPU_UTIL:-0.45}"
    sed "s/gpu_memory_utilization:[[:space:]]*0\.8/gpu_memory_utilization: ${VOX_UTIL}/" "$VOX_SRC" > "$VOX_RUNTIME"
    echo "[entrypoint] voxtral : gpu_memory_utilization stage0 → ${VOX_UTIL} (VOXTRAL_STAGE_GPU_UTIL)"
fi

echo "[entrypoint] data=${DATA_DIR}  vLLM=127.0.0.1:${VLLM_PORT:-8091}"
echo "[entrypoint] note : au 1er démarrage/switch, le sidecar charge le modèle (~3-5 min) ; /v1/audio/speech"
echo "[entrypoint]        model=qwen3 répond 503 pendant ce temps, Kokoro reste dispo."

exec supervisord -c /etc/supervisor/conf.d/voxmind.conf
