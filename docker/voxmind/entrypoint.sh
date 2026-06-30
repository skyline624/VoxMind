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

# Deploy-config vLLM ajusté : le pipeline Omni réserve la VRAM PAR STAGE (talker + code2wav, 0.3 chacun par
# défaut → ~20 Go). On abaisse ces valeurs (défaut 0.2 ≈ ~14-15 Go au total avec les CUDA graphs) ; réglable
# via QWEN3_STAGE_GPU_UTIL pour une carte partagée. Le flag CLI --gpu-memory-utilization ne suffit PAS (écrasé
# par le yaml), d'où la génération d'un deploy-config dérivé de celui d'upstream (reste en phase avec les MAJ).
DEPLOY_SRC=/app/vllm-omni/vllm_omni/deploy/qwen3_tts.yaml
DEPLOY_RUNTIME=/app/qwen3_tts.runtime.yaml
STAGE_UTIL="${QWEN3_STAGE_GPU_UTIL:-0.2}"
sed "s/gpu_memory_utilization:[[:space:]]*[0-9.]\+/gpu_memory_utilization: ${STAGE_UTIL}/g" "$DEPLOY_SRC" > "$DEPLOY_RUNTIME"
echo "[entrypoint] deploy-config : gpu_memory_utilization par stage = ${STAGE_UTIL} (QWEN3_STAGE_GPU_UTIL)"

echo "[entrypoint] models=${MODELS_DIR}  data=${DATA_DIR}  vLLM=127.0.0.1:${VLLM_PORT:-8091}  modèle=${QWEN3_MODEL:-?}"
echo "[entrypoint] note : au 1er démarrage, le sidecar vLLM charge le modèle (~3-5 min, capture CUDA graphs)."
echo "[entrypoint]        pendant cette fenêtre, /v1/audio/speech model=qwen3 répond 503 ; Kokoro reste dispo."

exec supervisord -c /etc/supervisor/conf.d/voxmind.conf
