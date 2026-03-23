#!/usr/bin/env python3
"""
VoxMind - Serveur gRPC Parakeet (NVIDIA NeMo ASR)
Transcription vocale légère via nvidia/parakeet-ctc-1.1b.

Prérequis :
  - pip install nemo_toolkit[asr]
  - Générer le code gRPC : python -m grpc_tools.protoc -I../protos --python_out=. --grpc_python_out=. ../protos/parakeet.proto

Usage :
  python parakeet_server.py --port 50053
  python parakeet_server.py --port 50053 --models-path /home/pc/voice_data/cache
"""

import argparse
import io
import logging
import os
import struct
import sys
from concurrent import futures

import grpc
import numpy as np

try:
    import parakeet_pb2
    import parakeet_pb2_grpc
except ImportError:
    print("ERREUR: Fichiers gRPC non générés. Exécuter :")
    print("  python -m grpc_tools.protoc -I../protos --python_out=. --grpc_python_out=. ../protos/parakeet.proto")
    sys.exit(1)

logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(message)s")
logger = logging.getLogger(__name__)

MODEL_NAME = "nvidia/parakeet-ctc-1.1b"
MODEL_VERSION = "1.1b"

_model = None
_model_loaded = False


def load_model(models_path: str):
    """Charge le modèle Parakeet depuis le cache ou HuggingFace."""
    global _model, _model_loaded
    try:
        import nemo.collections.asr as nemo_asr

        nemo_path = os.path.join(models_path, "parakeet", "parakeet-ctc-1.1b.nemo")
        if os.path.exists(nemo_path):
            logger.info("Chargement Parakeet depuis %s", nemo_path)
            _model = nemo_asr.models.EncDecCTCModelBPE.restore_from(nemo_path)
        else:
            logger.info("Téléchargement Parakeet depuis HuggingFace (%s)...", MODEL_NAME)
            _model = nemo_asr.models.EncDecCTCModelBPE.from_pretrained(MODEL_NAME)

        _model.eval()
        _model_loaded = True
        logger.info("Parakeet chargé avec succès.")
    except Exception as e:
        logger.error("Impossible de charger Parakeet : %s", e)
        _model_loaded = False


def wav_bytes_to_float32(audio_bytes: bytes, sample_rate: int = 16000) -> np.ndarray:
    """Convertit des bytes WAV PCM 16-bit en tableau float32 normalisé."""
    # Tenter de parser l'en-tête WAV; sinon traiter comme PCM brut
    try:
        import soundfile as sf
        buf = io.BytesIO(audio_bytes)
        data, _ = sf.read(buf, dtype="float32")
        if data.ndim > 1:
            data = data.mean(axis=1)
        return data
    except Exception:
        # Fallback : PCM 16-bit little-endian
        n_samples = len(audio_bytes) // 2
        samples = struct.unpack(f"<{n_samples}h", audio_bytes[:n_samples * 2])
        return np.array(samples, dtype=np.float32) / 32768.0


class ParakeetTranscriptionServicer(parakeet_pb2_grpc.ParakeetTranscriptionServicer):

    def Transcribe(self, request, context):
        if not _model_loaded or _model is None:
            context.set_code(grpc.StatusCode.UNAVAILABLE)
            context.set_details("Modèle Parakeet non chargé.")
            return parakeet_pb2.TranscriptionResult()

        try:
            audio = wav_bytes_to_float32(request.audio_data, request.sample_rate or 16000)
            transcriptions = _model.transcribe([audio])
            text = transcriptions[0] if transcriptions else ""
            logger.debug("Transcription : %s", text[:80])
            return parakeet_pb2.TranscriptionResult(
                text=text,
                confidence=0.95,
                language="en"
            )
        except Exception as e:
            logger.error("Erreur transcription : %s", e)
            context.set_code(grpc.StatusCode.INTERNAL)
            context.set_details(str(e))
            return parakeet_pb2.TranscriptionResult()

    def GetModelInfo(self, request, context):
        return parakeet_pb2.ModelInfo(
            model_name=MODEL_NAME,
            version=MODEL_VERSION,
            is_loaded=_model_loaded
        )


def serve(port: int, models_path: str):
    load_model(models_path)

    server = grpc.server(futures.ThreadPoolExecutor(max_workers=4))
    parakeet_pb2_grpc.add_ParakeetTranscriptionServicer_to_server(
        ParakeetTranscriptionServicer(), server
    )
    server.add_insecure_port(f"[::]:{port}")
    server.start()
    logger.info("Serveur Parakeet démarré sur port %d (modèle chargé=%s)", port, _model_loaded)

    try:
        server.wait_for_termination()
    except KeyboardInterrupt:
        logger.info("Arrêt du serveur Parakeet.")
        server.stop(grace=5)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="VoxMind Parakeet gRPC Server")
    parser.add_argument("--port", type=int, default=50053, help="Port gRPC (défaut: 50053)")
    parser.add_argument(
        "--models-path",
        default=os.path.expanduser("~/voice_data/cache"),
        help="Répertoire des modèles"
    )
    args = parser.parse_args()
    serve(args.port, args.models_path)
