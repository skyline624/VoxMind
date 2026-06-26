# ── Build stage ───────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore en couche cache : copier d'abord les csproj du graphe de l'API.
COPY src/VoxMind.Core/VoxMind.Core.csproj         src/VoxMind.Core/
COPY src/VoxMind.Api/VoxMind.Api.csproj           src/VoxMind.Api/
COPY src/VoxMind.Parakeet/VoxMind.Parakeet.csproj src/VoxMind.Parakeet/
COPY src/VoxMind.F5Tts/VoxMind.F5Tts.csproj       src/VoxMind.F5Tts/
RUN dotnet restore src/VoxMind.Api/VoxMind.Api.csproj

# Build + publish (CopySherpaOnnxRuntime target inclut le natif sherpa linux-x64).
COPY src/ ./src/
RUN dotnet publish src/VoxMind.Api/VoxMind.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

# Forcer la libonnxruntime.so de sherpa-onnx : le publish copie sinon celle de
# Microsoft.ML.OnnxRuntime (plus ancienne), mais libsherpa-onnx-c-api.so exige le
# symbole VERS_1.23.2 -> sinon DllNotFound + crash du finalizer au runtime.
RUN set -e; \
    SRC=$(find /root/.nuget/packages/org.k2fsa.sherpa.onnx.runtime.linux-x64 -name 'libonnxruntime.so*' -type f | head -1); \
    echo "sherpa onnxruntime -> $SRC"; \
    cp -f "$SRC" /app/publish/runtimes/linux-x64/native/libonnxruntime.so

# ── Runtime stage ─────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# ffmpeg : décodage audio multi-format (MP3/WAV/OGG/Opus/WebM)
# libgomp1/libstdc++6 : requis par onnxruntime + sherpa-onnx natifs
RUN apt-get update && apt-get install -y --no-install-recommends \
    ffmpeg libgomp1 libstdc++6 libportaudio2 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .
COPY docker/config.json /app/data/config/config.json

# Les modèles sont montés en volume sur /app/models (cf. docker-compose).
ENV VOXMIND_DATA_DIR=/app/data \
    VOXMIND_MODELS_DIR=/app/models \
    ASPNETCORE_URLS=

EXPOSE 8090
ENTRYPOINT ["dotnet", "VoxMind.Api.dll"]
