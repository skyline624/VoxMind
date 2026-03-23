# VoxMind

Système local de transcription vocale en temps réel avec identification automatique des locuteurs.

## Fonctionnalités

- Transcription vocale temps réel (Whisper)
- Identification des locuteurs (PyAnnote)
- Mode écoute continu avec résumés automatiques
- Communication externe via bridge JSON file-based (Cortana/OpenClaw)
- Support multi-plateforme : CPU, CUDA (NVIDIA), ROCm (AMD Linux)

## Prérequis

- .NET 8.0+
- Python 3.10+
- PortAudio (`libportaudio2`, Linux) ou NAudio (Windows)
- `ffmpeg`
- Token HuggingFace (pour PyAnnote)
- CUDA 12.x (optionnel, pour accélération GPU NVIDIA)

## Installation Rapide

```bash
# 1. Installer les dépendances système (Ubuntu/Debian)
sudo apt-get install -y python3 python3-venv python3-pip portaudio19-dev libportaudio2 ffmpeg

# 2. Lancer le script d'installation
chmod +x install.sh
./install.sh

# 3. Configurer le token HuggingFace
export HUGGINGFACE_TOKEN=votre_token

# 4. Télécharger les modèles
python python_services/download_models.py --whisper base --pyannote

# 5. Lancer VoxMind
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
VoxMind (C# .NET 8)  ←→  Python Services (gRPC)
        │                        │
   WhisperService           PyAnnote Server
   ParakeetService           (port 50051)
   SessionManager        Parakeet ASR Server
   FileBridge (JSON)         (port 50053)
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

## Moteurs de Transcription

| Moteur | Modèle | CPU-friendly | Précision |
|--------|--------|-------------|-----------|
| **Parakeet** (défaut) | `nvidia/parakeet-ctc-1.1b` | Oui | Excellente (anglais) |
| **Whisper** | `Whisper.net` (ggml) | Moyen | Très bonne (multilingue) |

Configurer dans `appsettings.json` :
```json
"Ml": {
  "Transcription": {
    "Engine": "parakeet",
    "ParakeetEndpoint": "localhost:50053"
  }
}
```

Démarrer le service Parakeet avant le serveur VoxMind :
```bash
python python_services/parakeet_server.py --port 50053
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
