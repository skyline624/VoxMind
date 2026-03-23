#!/usr/bin/env python3
"""
VoxMind - Téléchargement des modèles ML

Usage :
  python download_models.py --whisper base --pyannote --output /home/pc/voice_data/cache
  python download_models.py --whisper tiny   # Seulement Whisper Tiny
  python download_models.py --pyannote       # Seulement PyAnnote
  python download_models.py --parakeet       # Seulement Parakeet (nvidia/parakeet-ctc-1.1b)
"""

import argparse
import os
import sys

WHISPER_MODELS = {"tiny", "base", "small", "medium", "large"}


def download_whisper(size: str, output_dir: str):
    """Télécharge un modèle Whisper via openai-whisper."""
    if size not in WHISPER_MODELS:
        print(f"ERREUR: Taille Whisper invalide. Valeurs possibles : {', '.join(sorted(WHISPER_MODELS))}")
        sys.exit(1)

    print(f"Téléchargement du modèle Whisper '{size}'...")
    try:
        import whisper
        model_dir = os.path.join(output_dir, "whisper")
        os.makedirs(model_dir, exist_ok=True)
        # openai-whisper télécharge dans ~/.cache/whisper par défaut
        # On peut le diriger via la variable WHISPER_CACHE_DIR ou en passant download_root
        model = whisper.load_model(size, download_root=model_dir)
        print(f"Whisper '{size}' téléchargé dans {model_dir}")
        del model
    except ImportError:
        print("ERREUR: openai-whisper non installé. Exécuter : pip install openai-whisper")
        sys.exit(1)


def download_pyannote(output_dir: str):
    """Télécharge les modèles PyAnnote."""
    hf_token = os.environ.get("HUGGINGFACE_TOKEN")
    if not hf_token:
        print("ERREUR: HUGGINGFACE_TOKEN n'est pas défini.")
        print("Obtenir un token sur https://huggingface.co/settings/tokens")
        print("Puis accepter les conditions d'utilisation de :")
        print("  - https://huggingface.co/pyannote/speaker-diarization-3.1")
        print("  - https://huggingface.co/pyannote/embedding")
        sys.exit(1)

    print("Téléchargement des modèles PyAnnote...")
    try:
        from pyannote.audio import Model, Pipeline

        pyannote_dir = os.path.join(output_dir, "pyannote")
        os.makedirs(pyannote_dir, exist_ok=True)

        # Embedding model
        print("  - Téléchargement pyannote/embedding...")
        emb_model = Model.from_pretrained("pyannote/embedding", use_auth_token=hf_token)
        print(f"  PyAnnote embedding téléchargé. Modèle chargé en mémoire.")
        del emb_model

        print("Modèles PyAnnote téléchargés avec succès.")
    except ImportError:
        print("ERREUR: pyannote.audio non installé. Exécuter : pip install pyannote.audio")
        sys.exit(1)
    except Exception as e:
        print(f"ERREUR lors du téléchargement PyAnnote: {e}")
        sys.exit(1)


def download_parakeet(output_dir: str):
    """Télécharge le modèle Parakeet via NVIDIA NeMo."""
    print("Téléchargement du modèle Parakeet (nvidia/parakeet-ctc-1.1b)...")
    try:
        import nemo.collections.asr as nemo_asr

        parakeet_dir = os.path.join(output_dir, "parakeet")
        os.makedirs(parakeet_dir, exist_ok=True)

        model = nemo_asr.models.EncDecCTCModelBPE.from_pretrained("nvidia/parakeet-ctc-1.1b")
        model_path = os.path.join(parakeet_dir, "parakeet-ctc-1.1b.nemo")
        model.save_to(model_path)
        print(f"Parakeet téléchargé dans {model_path}")
        del model
    except ImportError:
        print("ERREUR: nemo_toolkit non installé. Exécuter : pip install nemo_toolkit[asr]")
        sys.exit(1)
    except Exception as e:
        print(f"ERREUR lors du téléchargement Parakeet: {e}")
        sys.exit(1)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="VoxMind - Téléchargement des modèles ML")
    parser.add_argument(
        "--whisper",
        choices=list(WHISPER_MODELS),
        help="Taille du modèle Whisper à télécharger"
    )
    parser.add_argument(
        "--pyannote",
        action="store_true",
        help="Télécharger les modèles PyAnnote (requiert HUGGINGFACE_TOKEN)"
    )
    parser.add_argument(
        "--parakeet",
        action="store_true",
        help="Télécharger le modèle Parakeet (nvidia/parakeet-ctc-1.1b)"
    )
    parser.add_argument(
        "--output",
        default=os.path.expanduser("~/voice_data/cache"),
        help="Répertoire de sortie des modèles"
    )
    args = parser.parse_args()

    if not args.whisper and not args.pyannote and not args.parakeet:
        parser.print_help()
        sys.exit(1)

    os.makedirs(args.output, exist_ok=True)

    if args.whisper:
        download_whisper(args.whisper, args.output)

    if args.pyannote:
        download_pyannote(args.output)

    if args.parakeet:
        download_parakeet(args.output)

    print("\nTéléchargement terminé !")
