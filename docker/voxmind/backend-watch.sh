#!/usr/bin/env bash
# Surveille le fichier d'état du backend TTS (écrit par VoxMind pour demander une bascule qwen3↔voxtral).
# Quand il change, tue le sidecar vLLM ; supervisord relance alors serve-tts.sh, qui relit le fichier et
# sert le nouveau modèle. VoxMind ne gère ainsi AUCUN process : il écrit juste le backend voulu.
set -u
STATE_FILE="${VOXMIND_DATA_DIR:-/app/voice_data}/config/tts_backend"

read_backend() { cat "$STATE_FILE" 2>/dev/null | tr -d '[:space:]'; }

prev="$(read_backend)"
[ -z "$prev" ] && prev="${TTS_BACKEND:-qwen3}"
echo "[backend-watch] démarrage, backend courant = ${prev}"

while true; do
    cur="$(read_backend)"
    if [ -n "$cur" ] && [ "$cur" != "$prev" ]; then
        echo "[backend-watch] bascule ${prev} → ${cur} : rechargement du sidecar vLLM"
        prev="$cur"
        pkill -9 -f "vllm" 2>/dev/null || true           # tue API server + stage workers ; supervisord relance serve-tts.sh
    fi
    sleep 2
done
