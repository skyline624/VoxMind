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
   SessionManager            (port 50051)
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

## Licence

MIT — Voir [LICENSE](LICENSE)
