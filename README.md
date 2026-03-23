# VoxMind

Système local de transcription vocale en temps réel avec identification automatique des locuteurs.

## Fonctionnalités

- Transcription vocale temps réel (Parakeet TDT ONNX — 100% local, sans Python)
- Identification des locuteurs (sherpa-onnx — 100% local, sans Python)
- Mode écoute continu avec résumés automatiques
- Communication externe via bridge JSON file-based (Cortana/OpenClaw)
- Support multi-plateforme : CPU (x64)

## Prérequis

- .NET 8.0+
- PortAudio (`libportaudio2`, Linux) ou NAudio (Windows)

## Installation Rapide

```bash
# 1. Installer les dépendances système (Ubuntu/Debian)
sudo apt-get install -y portaudio19-dev libportaudio2

# 2. Télécharger les modèles Parakeet TDT (HuggingFace)
mkdir -p models/parakeet-tdt-0.6b-v3-int8
cd models/parakeet-tdt-0.6b-v3-int8
wget https://huggingface.co/smcleod/parakeet-tdt-0.6b-v3-int8/resolve/main/nemo128.onnx
wget https://huggingface.co/smcleod/parakeet-tdt-0.6b-v3-int8/resolve/main/encoder-model.int8.onnx
wget https://huggingface.co/smcleod/parakeet-tdt-0.6b-v3-int8/resolve/main/decoder_joint-model.int8.onnx
wget https://huggingface.co/smcleod/parakeet-tdt-0.6b-v3-int8/resolve/main/vocab.txt
cd ../..

# 3. Télécharger le modèle sherpa-onnx (speaker recognition)
mkdir -p models
wget -O models/3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx \
  https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx

# 4. Lancer VoxMind
dotnet run --project src/VoxMind.CLI
```

## Commandes CLI

```bash
./voxmind start --name "reunion"   # Démarrer une session
./voxmind status                   # Statut en cours
./voxmind stop                     # Arrêter et générer le résumé
./voxmind transcribe audio.wav     # Transcrire un fichier
./voxmind enroll "Marjorie"        # Enregistrer une voix
./voxmind list-speakers            # Voir les profils
```

## Architecture

```
VoxMind (C# .NET 8) — 100% local, sans Python
        │
   ParakeetOnnxTranscriptionService   (Microsoft.ML.OnnxRuntime)
   SherpaOnnxSpeakerService           (org.k2fsa.sherpa.onnx)
   SessionManager
   FileBridge (JSON)
   SQLite Database
```

Voir [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) pour les détails.

## Communication Externe (Bridge)

```bash
# Depuis Cortana/OpenClaw
echo '{"command":"START_LISTENING","parameters":{"session_name":"reunion"}}' \
  > voice_data/shared/commands_to_voxmind.json

cat voice_data/shared/status_from_voxmind.json
```

## Transcription

Moteur unique : **Parakeet TDT ONNX** (`smcleod/parakeet-tdt-0.6b-v3-int8`), inférence locale via `Microsoft.ML.OnnxRuntime`.

Configurer le chemin du modèle dans `appsettings.json` :
```json
"Ml": {
  "Transcription": {
    "ParakeetModelPath": "./models/parakeet-tdt-0.6b-v3-int8"
  }
}
```

## Identification des Locuteurs

Moteur : **sherpa-onnx** (`org.k2fsa.sherpa.onnx`), embedding ERes2Net local.

Configurer dans `appsettings.json` :
```json
"Ml": {
  "SpeakerRecognition": {
    "Enabled": true,
    "ConfidenceThreshold": 0.7,
    "SherpaOnnx": {
      "EmbeddingModelPath": "./models/3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx",
      "NumThreads": 4
    }
  }
}
```

---

## Downloads

### Latest Release
[![GitHub release (latest by date)](https://img.shields.io/github/v/release/skyline624/VoxMind)](https://github.com/skyline624/VoxMind/releases/latest)

### VoxMind Server
| Platform | Architecture | Download |
|----------|--------------|----------|
| Windows | x64 | [VoxMind.Server-win-x64.zip](https://github.com/skyline624/VoxMind/releases/latest/download/VoxMind.Server-win-x64.zip) |
| Linux | x64 | [VoxMind.Server-linux-x64.tar.gz](https://github.com/skyline624/VoxMind/releases/latest/download/VoxMind.Server-linux-x64.tar.gz) |
| macOS | x64 | [VoxMind.Server-osx-x64.zip](https://github.com/skyline624/VoxMind/releases/latest/download/VoxMind.Server-osx-x64.zip) |

### VoxMind ClientLite
| Platform | Architecture | Download |
|----------|--------------|----------|
| Windows | x64 | [VoxMind.ClientLite-win-x64.zip](https://github.com/skyline624/VoxMind/releases/latest/download/VoxMind.ClientLite-win-x64.zip) |
| Linux | x64 | [VoxMind.ClientLite-linux-x64.tar.gz](https://github.com/skyline624/VoxMind/releases/latest/download/VoxMind.ClientLite-linux-x64.tar.gz) |
| macOS | x64 | [VoxMind.ClientLite-osx-x64.zip](https://github.com/skyline624/VoxMind/releases/latest/download/VoxMind.ClientLite-osx-x64.zip) |

## Installation

### Server
```bash
# Windows
unzip VoxMind.Server-win-x64.zip
./VoxMind.Server.exe

# Linux
tar -xzf VoxMind.Server-linux-x64.tar.gz
./VoxMind.Server
```

### ClientLite
```bash
# Windows
unzip VoxMind.ClientLite-win-x64.zip
./VoxMind.ClientLite.exe configure --server http://server:50052 --name "My PC"
./VoxMind.ClientLite.exe start

# Linux
tar -xzf VoxMind.ClientLite-linux-x64.tar.gz
./VoxMind.ClientLite configure --server http://server:50052 --name "My PC"
./VoxMind.ClientLite start
```

---

## Licence

MIT — Voir [LICENSE](LICENSE)
