#!/bin/bash
# VoxMind - Script d'installation (100% C#, sans Python)
# Usage: chmod +x install.sh && ./install.sh

set -e

VOXMIND_DIR="$(cd "$(dirname "$0")" && pwd)"
MODELS_DIR="$VOXMIND_DIR/models"
VOICE_DATA_DIR="$VOXMIND_DIR/voice_data"

echo "==================================="
echo "  VoxMind — Installation"
echo "==================================="
echo ""

# 1. Détection du gestionnaire de paquets
if command -v apt-get &>/dev/null; then
    PKG_MANAGER="apt"
elif command -v pacman &>/dev/null; then
    PKG_MANAGER="pacman"
elif command -v dnf &>/dev/null; then
    PKG_MANAGER="dnf"
else
    echo "ERREUR: Gestionnaire de paquets non détecté (apt/pacman/dnf requis)."
    exit 1
fi

echo "Gestionnaire de paquets: $PKG_MANAGER"

# 2. Installation des prérequis système
echo ""
echo "Installation des prérequis système..."
if [ "$PKG_MANAGER" = "apt" ]; then
    sudo apt-get update
    sudo apt-get install -y \
        portaudio19-dev libportaudio2 libsndfile1 \
        ffmpeg
elif [ "$PKG_MANAGER" = "pacman" ]; then
    sudo pacman -Sy --noconfirm portaudio ffmpeg libsndfile
elif [ "$PKG_MANAGER" = "dnf" ]; then
    sudo dnf install -y portaudio-devel ffmpeg libsndfile
fi

# 3. Vérification .NET 8 SDK
echo ""
if ! command -v dotnet &>/dev/null || ! dotnet --version | grep -q "^8\."; then
    echo "Installation de .NET 8 SDK..."
    if [ "$PKG_MANAGER" = "apt" ]; then
        wget -q https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb -O /tmp/packages-microsoft-prod.deb
        sudo dpkg -i /tmp/packages-microsoft-prod.deb
        sudo apt-get update
        sudo apt-get install -y dotnet-sdk-8.0
    else
        echo "AVERTISSEMENT: Installer .NET 8 SDK manuellement depuis https://dotnet.microsoft.com/download"
        echo "Puis relancer ce script."
        exit 1
    fi
else
    echo ".NET $(dotnet --version) détecté."
fi

# 4. Création de la structure de données
echo ""
echo "Création de la structure de données..."
mkdir -p "$VOICE_DATA_DIR"/{profiles/backups,sessions,shared,logs,config}

# Copier config.json template si n'existe pas
if [ ! -f "$VOICE_DATA_DIR/config/config.json" ]; then
    if [ -f "$VOXMIND_DIR/voice_data/config/config.json" ]; then
        cp "$VOXMIND_DIR/voice_data/config/config.json" "$VOICE_DATA_DIR/config/config.json"
        echo "Configuration copiée vers $VOICE_DATA_DIR/config/config.json"
    fi
fi

# 5. Téléchargement des modèles ONNX
echo ""
echo "Téléchargement des modèles ML..."

# Parakeet TDT (transcription)
PARAKEET_DIR="$MODELS_DIR/parakeet-tdt-0.6b-v3-int8"
if [ ! -f "$PARAKEET_DIR/nemo128.onnx" ]; then
    echo "  Téléchargement Parakeet TDT-0.6b-v3-int8 (~500MB)..."
    mkdir -p "$PARAKEET_DIR"

    wget -q --show-progress -O "$PARAKEET_DIR/nemo128.onnx" \
        "https://huggingface.co/smcleod/parakeet-tdt-0.6b-v3-int8/resolve/main/nemo128.onnx"

    wget -q --show-progress -O "$PARAKEET_DIR/encoder-model.int8.onnx" \
        "https://huggingface.co/smcleod/parakeet-tdt-0.6b-v3-int8/resolve/main/encoder-model.int8.onnx"

    wget -q --show-progress -O "$PARAKEET_DIR/decoder_joint-model.int8.onnx" \
        "https://huggingface.co/smcleod/parakeet-tdt-0.6b-v3-int8/resolve/main/decoder_joint-model.int8.onnx"

    wget -q --show-progress -O "$PARAKEET_DIR/vocab.txt" \
        "https://huggingface.co/smcleod/parakeet-tdt-0.6b-v3-int8/resolve/main/vocab.txt"

    echo "  Parakeet TDT téléchargé."
else
    echo "  Parakeet TDT déjà présent."
fi

# sherpa-onnx 3DSpeaker (speaker embedding)
SHERPA_MODEL="$MODELS_DIR/3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx"
if [ ! -f "$SHERPA_MODEL" ]; then
    echo "  Téléchargement 3DSpeaker embedding model (~280MB)..."
    mkdir -p "$MODELS_DIR"

    wget -q --show-progress -O "$SHERPA_MODEL" \
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx"

    echo "  3DSpeaker téléchargé."
else
    echo "  3DSpeaker déjà présent."
fi

# 5b. F5-TTS (synthèse vocale) — facultatif
# Pas de checkpoints publics tout-prêts pour FR ; voir docs/F5TtsExport.md pour
# la procédure d'export Python (one-shot). On crée juste les répertoires
# attendus pour que le service démarre en graceful degradation.
F5_DIR="$MODELS_DIR/f5-tts"
if [ ! -d "$F5_DIR" ]; then
    echo ""
    echo "F5-TTS : création de l'arborescence (modèles à exporter via docs/F5TtsExport.md)..."
    mkdir -p "$F5_DIR/fr" "$F5_DIR/en"
    cat > "$F5_DIR/README.md" << 'EOF'
# F5-TTS checkpoints

Ce dossier doit contenir les checkpoints F5-TTS-ONNX par langue :

```
fr/
  ├─ F5_Preprocess.onnx
  ├─ F5_Transformer.onnx
  ├─ F5_Decode.onnx
  ├─ tokens.txt
  └─ reference.wav
en/
  ├─ F5_Preprocess.onnx
  ├─ ... (idem)
```

Procédure d'export depuis les checkpoints PyTorch communautaires :
voir [`docs/F5TtsExport.md`](../../docs/F5TtsExport.md) à la racine du repo.

Tant que ces fichiers ne sont pas présents, l'API `/v1/audio/speech` répond
HTTP 503 avec un message explicatif et la commande `voxmind speak` exit 13.
EOF
    echo "  Arborescence F5-TTS créée. Suivre docs/F5TtsExport.md pour exporter les modèles."
fi

# 6. Build .NET
echo ""
echo "Build du projet .NET..."
cd "$VOXMIND_DIR"
dotnet restore VoxMind.sln
dotnet build VoxMind.sln -c Release
echo "Build réussi."

# 7. Résumé
echo ""
echo "==================================="
echo "  Installation terminée !"
echo "==================================="
echo ""
echo "Structure créée :"
echo "  - $MODELS_DIR/parakeet-tdt-0.6b-v3-int8/"
echo "  - $MODELS_DIR/3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx"
echo "  - $MODELS_DIR/f5-tts/{fr,en}/  (à remplir via docs/F5TtsExport.md)"
echo "  - $VOICE_DATA_DIR/{profiles,sessions,shared,logs,config}"
echo ""
echo "Démarrer VoxMind :"
echo "  dotnet run --project src/VoxMind.CLI"
echo ""
echo "Ou avec variable d'environnement pour les données :"
echo "  VOXMIND_DATA_DIR=/chemin/vers/donnees dotnet run --project src/VoxMind.CLI"