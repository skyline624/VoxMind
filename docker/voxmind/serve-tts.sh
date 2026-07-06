#!/usr/bin/env bash
# Lance le sidecar vLLM-omni selon TTS_BACKEND :
#   qwen3   → Qwen3-TTS   (clonage possible, deploy-config ajusté par l'entrypoint)
#   voxtral → Voxtral-TTS (20 voix presets, PAS de clonage sur le checkpoint public)
# Un seul modèle à la fois (chaque pipeline réserve ~20 Go sur une 24 Go).
set -e
PORT="${VLLM_PORT:-8091}"
# Le backend actif vient du FICHIER D'ÉTAT (écrit par VoxMind pour une bascule à chaud) ; à défaut, l'env.
STATE_FILE="${VOXMIND_DATA_DIR:-/app/voice_data}/config/tts_backend"
BACKEND="$(cat "$STATE_FILE" 2>/dev/null | tr -d '[:space:]')"
[ -z "$BACKEND" ] && BACKEND="${TTS_BACKEND:-qwen3}"

if [ "$BACKEND" = "voxtral" ]; then
    MODEL="${VOXTRAL_MODEL:-mistralai/Voxtral-4B-TTS-2603}"
    echo "[serve-tts] backend=voxtral → ${MODEL}"
    exec vllm-omni serve "${MODEL}" \
        --deploy-config /app/vllm-omni/vllm_omni/deploy/voxtral_tts.yaml \
        --host 127.0.0.1 --port "${PORT}" --omni
else
    echo "[serve-tts] backend=qwen3 → ${QWEN3_MODEL}"
    exec vllm-omni serve "${QWEN3_MODEL}" \
        --deploy-config /app/qwen3_tts.runtime.yaml \
        --host 127.0.0.1 --port "${PORT}" \
        --gpu-memory-utilization "${VLLM_GPU_MEM_UTIL:-0.5}" --trust-remote-code --omni
fi
