#!/usr/bin/env python3
"""
VoxMind - Serveur gRPC PyAnnote
Service d'extraction d'embeddings vocaux et d'identification des locuteurs.

Prérequis :
  - HUGGINGFACE_TOKEN env var (pour télécharger les modèles pyannote)
  - pip install -r requirements.txt

Usage :
  python pyannote_server.py --port 50051
  python pyannote_server.py --port 50051 --models-path /home/pc/voice_data/cache
"""

import argparse
import logging
import os
import struct
import sys
from concurrent import futures

import grpc
import numpy as np

# Importer les fichiers générés depuis speaker.proto
# Générer avec : python -m grpc_tools.protoc -I./protos --python_out=. --grpc_python_out=. protos/speaker.proto
try:
    import speaker_pb2
    import speaker_pb2_grpc
except ImportError:
    print("ERREUR: Fichiers gRPC non générés. Exécuter :")
    print("  python -m grpc_tools.protoc -I./protos --python_out=. --grpc_python_out=. protos/speaker.proto")
    sys.exit(1)

logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(message)s")
logger = logging.getLogger("voxmind.pyannote")

VERSION = "1.0.0"


class SpeakerRecognitionServicer(speaker_pb2_grpc.SpeakerRecognitionServicer):
    """Implémentation du service gRPC pour l'identification des locuteurs via PyAnnote."""

    def __init__(self, models_path: str, hf_token: str | None = None):
        self.models_path = models_path
        self.hf_token = hf_token or os.environ.get("HUGGINGFACE_TOKEN")
        self._embedding_model = None
        self._inference = None
        self._load_models()

    def _load_models(self):
        """Charge le modèle d'embedding PyAnnote."""
        try:
            from pyannote.audio import Model
            logger.info("Chargement du modèle PyAnnote speaker embedding...")

            if self.hf_token:
                self._embedding_model = Model.from_pretrained(
                    "pyannote/embedding",
                    use_auth_token=self.hf_token
                )
            else:
                logger.warning(
                    "HUGGINGFACE_TOKEN non défini. "
                    "Tentative de chargement depuis le cache local : %s",
                    self.models_path
                )
                # Tentative depuis cache local
                local_model_path = os.path.join(self.models_path, "pyannote", "embedding")
                if os.path.exists(local_model_path):
                    self._embedding_model = Model.from_pretrained(local_model_path)
                else:
                    raise RuntimeError(
                        "Modèle PyAnnote non trouvé localement. "
                        "Définir HUGGINGFACE_TOKEN ou télécharger le modèle."
                    )

            # Déplacer sur GPU si disponible
            import torch
            if torch.cuda.is_available():
                self._embedding_model = self._embedding_model.to("cuda")
                logger.info("Modèle PyAnnote chargé sur CUDA.")
            else:
                logger.info("Modèle PyAnnote chargé sur CPU.")

            # Créer l'instance Inference une seule fois (réutilisée à chaque appel)
            from pyannote.audio import Inference
            self._inference = Inference(self._embedding_model, window="whole")
            logger.info("Inference PyAnnote initialisée.")

        except ImportError:
            logger.error("pyannote.audio non installé. Exécuter: pip install pyannote.audio")
            raise
        except Exception as e:
            logger.error("Erreur lors du chargement du modèle PyAnnote: %s", e)
            raise

    def ExtractEmbedding(self, request, context):
        """Extrait un embedding vocal depuis des données WAV PCM 16kHz mono."""
        try:
            import io
            import soundfile as sf
            import torch

            # Décoder les bytes WAV
            audio_bytes = request.audio_data
            if not audio_bytes:
                context.set_code(grpc.StatusCode.INVALID_ARGUMENT)
                context.set_details("audio_data vide")
                return speaker_pb2.EmbeddingResponse(success=False, error="audio_data vide")

            # Lire l'audio depuis les bytes
            audio_buf = io.BytesIO(audio_bytes)
            waveform, sample_rate = sf.read(audio_buf, dtype="float32")

            if waveform.ndim == 1:
                waveform = waveform[np.newaxis, :]  # [1, samples]
            elif waveform.ndim == 2:
                waveform = waveform.T  # [channels, samples]

            # Convertir en tensor PyTorch
            waveform_tensor = torch.tensor(waveform).unsqueeze(0)  # [batch, channels, samples]
            if torch.cuda.is_available():
                waveform_tensor = waveform_tensor.cuda()

            # Extraire l'embedding (réutilise self._inference créé au démarrage)
            with torch.no_grad():
                embedding = self._inference({"waveform": waveform_tensor, "sample_rate": sample_rate})

            if hasattr(embedding, "data"):
                emb_array = embedding.data.flatten()
            else:
                emb_array = np.array(embedding).flatten()

            # Sérialiser le vecteur float32 en bytes
            emb_bytes = struct.pack(f"{len(emb_array)}f", *emb_array)

            return speaker_pb2.EmbeddingResponse(
                success=True,
                embedding=emb_bytes,
                duration_used=float(len(waveform[0]) / sample_rate)
            )

        except Exception as e:
            logger.error("Erreur ExtractEmbedding: %s", e, exc_info=True)
            return speaker_pb2.EmbeddingResponse(success=False, error=str(e))

    def CompareEmbeddings(self, request, context):
        """Compare deux embeddings et retourne la similarité cosinus."""
        try:
            # Désérialiser les embeddings
            n1 = len(request.embedding1) // 4
            n2 = len(request.embedding2) // 4

            emb1 = np.array(struct.unpack(f"{n1}f", request.embedding1))
            emb2 = np.array(struct.unpack(f"{n2}f", request.embedding2))

            # Similarité cosinus
            norm1 = np.linalg.norm(emb1)
            norm2 = np.linalg.norm(emb2)
            if norm1 == 0 or norm2 == 0:
                cosine_sim = 0.0
            else:
                cosine_sim = float(np.dot(emb1, emb2) / (norm1 * norm2))

            # Distance euclidienne
            euclidean_dist = float(np.linalg.norm(emb1 - emb2))

            # Seuil par défaut : 0.7 de similarité cosinus
            is_same = cosine_sim >= 0.7

            return speaker_pb2.CompareResponse(
                cosine_similarity=cosine_sim,
                euclidean_distance=euclidean_dist,
                is_same_speaker=is_same
            )

        except Exception as e:
            logger.error("Erreur CompareEmbeddings: %s", e, exc_info=True)
            context.set_code(grpc.StatusCode.INTERNAL)
            context.set_details(str(e))
            return speaker_pb2.CompareResponse()

    def Ping(self, request, context):
        """Health check."""
        return speaker_pb2.Pong(alive=True, version=VERSION)


def serve(port: int, models_path: str):
    """Démarre le serveur gRPC."""
    server = grpc.server(
        futures.ThreadPoolExecutor(max_workers=4),
        options=[
            ("grpc.max_receive_message_length", 50 * 1024 * 1024),   # 50 MB
            ("grpc.max_send_message_length", 10 * 1024 * 1024),       # 10 MB
        ]
    )

    servicer = SpeakerRecognitionServicer(models_path=models_path)
    speaker_pb2_grpc.add_SpeakerRecognitionServicer_to_server(servicer, server)

    server.add_insecure_port(f"[::]:{port}")
    server.start()
    logger.info("Serveur PyAnnote gRPC démarré sur port %d", port)
    logger.info("Appuyer sur Ctrl+C pour arrêter.")

    try:
        server.wait_for_termination()
    except KeyboardInterrupt:
        logger.info("Arrêt du serveur...")
        server.stop(grace=5)
        logger.info("Serveur arrêté.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="VoxMind PyAnnote gRPC Server")
    parser.add_argument("--port", type=int, default=50051, help="Port gRPC (défaut: 50051)")
    parser.add_argument(
        "--models-path",
        default=os.path.expanduser("~/voice_data/cache"),
        help="Répertoire des modèles ML"
    )
    args = parser.parse_args()

    serve(port=args.port, models_path=args.models_path)
