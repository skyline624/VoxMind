#!/usr/bin/env python3
"""
VoxMind - Mock du serveur PyAnnote gRPC
Retourne des embeddings aléatoires float32[512] pour tests/CI sans token HuggingFace.

Usage :
  python mock_pyannote_server.py --port 50051
"""

import argparse
import logging
import os
import random
import struct
import sys
from concurrent import futures

import grpc

try:
    import speaker_pb2
    import speaker_pb2_grpc
except ImportError:
    print("ERREUR: Fichiers gRPC non générés. Exécuter :")
    print("  python -m grpc_tools.protoc -I./protos --python_out=. --grpc_python_out=. protos/speaker.proto")
    sys.exit(1)

logging.basicConfig(level=logging.INFO, format="%(asctime)s [MOCK] %(message)s")
logger = logging.getLogger("voxmind.mock_pyannote")

EMBEDDING_DIM = 512
VERSION = "1.0.0-mock"


class MockSpeakerRecognitionServicer(speaker_pb2_grpc.SpeakerRecognitionServicer):
    """Mock : retourne des embeddings aléatoires reproductibles pour les tests."""

    def ExtractEmbedding(self, request, context):
        # Embedding aléatoire normalisé (simuler un vrai embedding)
        import math
        raw = [random.gauss(0, 1) for _ in range(EMBEDDING_DIM)]
        norm = math.sqrt(sum(x * x for x in raw))
        normalized = [x / norm for x in raw]

        emb_bytes = struct.pack(f"{EMBEDDING_DIM}f", *normalized)
        logger.info("ExtractEmbedding appelé, retourne un embedding mock de dim %d", EMBEDDING_DIM)

        return speaker_pb2.EmbeddingResponse(
            success=True,
            embedding=emb_bytes,
            duration_used=float(len(request.audio_data) / (16000 * 2))  # Estimation
        )

    def CompareEmbeddings(self, request, context):
        import math
        n1 = len(request.embedding1) // 4
        n2 = len(request.embedding2) // 4

        if n1 == 0 or n2 == 0:
            return speaker_pb2.CompareResponse(cosine_similarity=0.0, euclidean_distance=99.0, is_same_speaker=False)

        emb1 = struct.unpack(f"{n1}f", request.embedding1)
        emb2 = struct.unpack(f"{n2}f", request.embedding2[:n1 * 4])

        dot = sum(a * b for a, b in zip(emb1, emb2))
        norm1 = math.sqrt(sum(x * x for x in emb1))
        norm2 = math.sqrt(sum(x * x for x in emb2))
        cosine_sim = dot / (norm1 * norm2) if norm1 and norm2 else 0.0
        euclidean = math.sqrt(sum((a - b) ** 2 for a, b in zip(emb1, emb2)))

        return speaker_pb2.CompareResponse(
            cosine_similarity=cosine_sim,
            euclidean_distance=euclidean,
            is_same_speaker=cosine_sim >= 0.7
        )

    def Ping(self, request, context):
        return speaker_pb2.Pong(alive=True, version=VERSION)


def serve(port: int):
    server = grpc.server(futures.ThreadPoolExecutor(max_workers=2))
    speaker_pb2_grpc.add_SpeakerRecognitionServicer_to_server(
        MockSpeakerRecognitionServicer(), server
    )
    server.add_insecure_port(f"[::]:{port}")
    server.start()
    logger.info("Mock PyAnnote gRPC Server démarré sur port %d", port)

    try:
        server.wait_for_termination()
    except KeyboardInterrupt:
        server.stop(grace=2)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="VoxMind Mock PyAnnote Server")
    parser.add_argument("--port", type=int, default=50051)
    args = parser.parse_args()
    serve(args.port)
