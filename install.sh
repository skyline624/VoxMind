#!/bin/bash
# VoxMind - Script d'installation automatisé
# Usage: chmod +x install.sh && ./install.sh

set -e

VOXMIND_DIR="$(cd "$(dirname "$0")" && pwd)"
VOICE_DATA_DIR="${HOME}/voice_data"

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
        python3 python3-pip python3-venv \
        portaudio19-dev libportaudio2 libsndfile1 \
        ffmpeg
elif [ "$PKG_MANAGER" = "pacman" ]; then
    sudo pacman -Sy --noconfirm python python-pip portaudio ffmpeg
elif [ "$PKG_MANAGER" = "dnf" ]; then
    sudo dnf install -y python3 python3-pip portaudio-devel ffmpeg
fi

# 3. Vérification .NET 8
echo ""
if ! command -v dotnet &>/dev/null || ! dotnet --version | grep -q "^8\."; then
    echo "Installation de .NET 8..."
    if [ "$PKG_MANAGER" = "apt" ]; then
        wget -q https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb -O /tmp/packages-microsoft-prod.deb
        sudo dpkg -i /tmp/packages-microsoft-prod.deb
        sudo apt-get update
        sudo apt-get install -y dotnet-sdk-8.0
    else
        echo "AVERTISSEMENT: Installer .NET 8 manuellement depuis https://dotnet.microsoft.com/download"
        echo "Puis relancer ce script."
        exit 1
    fi
else
    echo ".NET $(dotnet --version) détecté."
fi

# 4. Environnement Python
echo ""
echo "Création de l'environnement Python..."
cd "$VOXMIND_DIR"
python3 -m venv venv
source venv/bin/activate
pip install --upgrade pip
pip install -r python_services/requirements.txt
echo "Environnement Python configuré."

# 5. Compilation des protos gRPC
echo ""
echo "Compilation des fichiers gRPC..."
cd python_services
python -m grpc_tools.protoc \
    -I./protos \
    --python_out=. \
    --grpc_python_out=. \
    protos/speaker.proto
cd ..
echo "Fichiers gRPC générés."

# 6. Création de la structure de données
echo ""
echo "Création de la structure de données..."
mkdir -p "$VOICE_DATA_DIR"/{profiles/backups,embeddings,sessions,shared,cache/whisper,logs,config}

if [ ! -f "$VOICE_DATA_DIR/config/config.json" ]; then
    cp "$VOXMIND_DIR/voice_data/config/config.json" "$VOICE_DATA_DIR/config/config.json"
    echo "Configuration copiée vers $VOICE_DATA_DIR/config/config.json"
fi

# 7. Build .NET
echo ""
echo "Build du projet .NET..."
dotnet restore VoxMind.sln
dotnet build VoxMind.sln -c Release
echo "Build réussi."

# 8. Téléchargement des modèles
echo ""
if [ -n "$HUGGINGFACE_TOKEN" ]; then
    echo "Téléchargement des modèles ML (Whisper Base + PyAnnote)..."
    source venv/bin/activate
    python python_services/download_models.py --whisper base --pyannote --output "$VOICE_DATA_DIR/cache"
else
    echo "AVERTISSEMENT: HUGGINGFACE_TOKEN non défini."
    echo "  → Téléchargement Whisper uniquement (pas PyAnnote)"
    source venv/bin/activate
    python python_services/download_models.py --whisper base --output "$VOICE_DATA_DIR/cache"
    echo ""
    echo "Pour activer l'identification des locuteurs :"
    echo "  1. Obtenir un token HF : https://huggingface.co/settings/tokens"
    echo "  2. Accepter les conditions : https://huggingface.co/pyannote/embedding"
    echo "  3. Exécuter : export HUGGINGFACE_TOKEN=<token>"
    echo "  4. Exécuter : python python_services/download_models.py --pyannote"
fi

# 9. Résumé
echo ""
echo "==================================="
echo "  Installation terminée !"
echo "==================================="
echo ""
echo "Démarrer VoxMind :"
echo "  # Démarrer le service PyAnnote (dans un terminal séparé)"
echo "  source venv/bin/activate"
echo "  python python_services/pyannote_server.py --port 50051"
echo ""
echo "  # Démarrer VoxMind CLI"
echo "  dotnet run --project src/VoxMind.CLI/VoxMind.CLI.csproj"
echo ""
echo "Ou avec Docker :"
echo "  HUGGINGFACE_TOKEN=<token> docker-compose up"
