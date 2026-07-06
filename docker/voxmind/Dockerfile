# syntax=docker/dockerfile:1
# ───────────────────────────────────────────────────────────────────────────────────────────────────────
# Image VoxMind TOUT-EN-UN : VoxMind.Api (.NET) + sidecar Qwen3-TTS (vLLM-omni), un seul conteneur GPU.
#   - VoxMind.Api écoute sur :8000 ; engines Kokoro/Parakeet/speaker en ONNX CPU, in-process.
#   - vLLM-omni sert Qwen3-TTS sur 127.0.0.1:8091 (GPU) ; VoxMind l'appelle en HTTP (moteur « qwen3 »).
#   Build (depuis la racine du repo) : docker build -f docker/voxmind/Dockerfile -t voxmind:aio .
# ───────────────────────────────────────────────────────────────────────────────────────────────────────

# ── Stage 1 : publication .NET (linux-x64 ; embarque sherpa-onnx + ONNX Runtime CPU via NuGet) ──
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dotnet-build
WORKDIR /src
COPY VoxMind.sln ./
COPY src/ ./src/
RUN dotnet restore src/VoxMind.Api/VoxMind.Api.csproj
RUN dotnet publish src/VoxMind.Api/VoxMind.Api.csproj -c Release -o /app/publish --no-restore

# ── Stage 2 : runtime = image officielle vLLM (CUDA+torch+vLLM pré-construits) + vllm-omni + runtime .NET ──
FROM vllm/vllm-openai:v0.23.0 AS final
ENV DEBIAN_FRONTEND=noninteractive

# Dépendances système : ffmpeg (décodage audio VoxMind), OpenMP/PortAudio/ICU (.NET + ONNX), supervisor, git.
RUN apt-get update && apt-get install -y --no-install-recommends \
        ffmpeg libgomp1 libportaudio2 ca-certificates git supervisor curl \
    && (apt-get install -y --no-install-recommends libicu74 || apt-get install -y --no-install-recommends libicu70) \
    && rm -rf /var/lib/apt/lists/*

# vllm-omni (wheel pur-Python ; fournit aussi vllm_omni/deploy/qwen3_tts.yaml utilisé par `serve`).
RUN git clone --depth 1 https://github.com/vllm-project/vllm-omni.git /app/vllm-omni \
    && pip install --no-cache-dir /app/vllm-omni soundfile

# Runtime ASP.NET .NET 10 copié depuis l'image officielle (pas de SDK dans l'image finale).
COPY --from=mcr.microsoft.com/dotnet/aspnet:10.0 /usr/share/dotnet /usr/share/dotnet
ENV PATH="/usr/share/dotnet:${PATH}" \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0

# Application + supervision + config par défaut.
COPY --from=dotnet-build /app/publish /app/voxmind
COPY docker/voxmind/supervisor/voxmind.conf /etc/supervisor/conf.d/voxmind.conf
COPY docker/voxmind/entrypoint.sh /app/entrypoint.sh
COPY docker/voxmind/serve-tts.sh /app/serve-tts.sh
COPY docker/voxmind/backend-watch.sh /app/backend-watch.sh
COPY docker/voxmind/config.default.json /app/config.default.json
RUN chmod +x /app/entrypoint.sh /app/serve-tts.sh /app/backend-watch.sh

ENV QWEN3_MODEL="Qwen/Qwen3-TTS-12Hz-1.7B-CustomVoice" \
    VOXMIND_MODELS_DIR="/app/models" \
    VOXMIND_DATA_DIR="/app/voice_data" \
    VLLM_PORT=8091 \
    VLLM_GPU_MEM_UTIL=0.5 \
    QWEN3_STAGE_GPU_UTIL=0.2 \
    TTS_BACKEND="qwen3" \
    QWEN3_TASK_TYPE="CustomVoice" \
    VOXTRAL_MODEL="mistralai/Voxtral-4B-TTS-2603" \
    VOXTRAL_VOICE="fr_female"

EXPOSE 8000
VOLUME ["/app/models", "/app/voice_data", "/root/.cache/huggingface"]

# Surcharge l'entrypoint de l'image vLLM (qui lance le serveur OpenAI) par notre superviseur.
ENTRYPOINT ["/app/entrypoint.sh"]
