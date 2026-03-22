#!/usr/bin/env python3
"""
VoxMind - Test manuel du client gRPC PyAnnote

Usage :
  python test_grpc_client.py                          # Tester contre localhost:50051
  python test_grpc_client.py --host localhost:50051
  python test_grpc_client.py --audio test_audio.wav
"""

import argparse
import io
import logging
import os
import struct
import sys
import wave

import grpc

try:
    import speaker_pb2
    import speaker_pb2_grpc
except ImportError:
    print("ERREUR: Fichiers gRPC non générés. Exécuter :")
    print("  python -m grpc_tools.protoc -I./protos --python_out=. --grpc_python_out=. protos/speaker.proto")
    sys.exit(1)

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger("test_grpc_client")


def generate_test_wav(duration_seconds: float = 3.0, sample_rate: int = 16000) -> bytes:
    """Génère un fichier WAV PCM 16kHz mono de silence (pour tester sans micro)."""
    import math
    import random

    num_samples = int(sample_rate * duration_seconds)
    # Bruit léger pour éviter le silence total
    samples = [int(random.gauss(0, 100)) for _ in range(num_samples)]

    buf = io.BytesIO()
    with wave.open(buf, "wb") as wf:
        wf.setnchannels(1)
        wf.setsampwidth(2)          # 16-bit
        wf.setframerate(sample_rate)
        wf.writeframes(struct.pack(f"{num_samples}h", *samples))

    return buf.getvalue()


def test_ping(stub):
    print("\n=== Test Ping ===")
    response = stub.Ping(speaker_pb2.Empty())
    print(f"  alive  : {response.alive}")
    print(f"  version: {response.version}")
    assert response.alive, "ÉCHEC: Ping retourne alive=false"
    print("  SUCCÈS")


def test_extract_embedding(stub, audio_data: bytes):
    print("\n=== Test ExtractEmbedding ===")
    request = speaker_pb2.AudioData(
        audio_data=audio_data,
        sample_rate=16000.0,
        duration_ms=int(len(audio_data) / (16000 * 2 / 1000))
    )
    response = stub.ExtractEmbedding(request)

    if not response.success:
        print(f"  ÉCHEC: {response.error}")
        return None

    # Vérifier que l'embedding est un vecteur float32
    embedding_bytes = response.embedding
    n = len(embedding_bytes) // 4
    embedding = struct.unpack(f"{n}f", embedding_bytes)

    print(f"  success       : {response.success}")
    print(f"  embedding_dim : {n}")
    print(f"  duration_used : {response.duration_used:.2f}s")
    print(f"  embedding[0:3]: {list(embedding[:3])}")
    assert n > 0, "ÉCHEC: Embedding vide"
    print("  SUCCÈS")
    return embedding_bytes


def test_compare_embeddings(stub, emb1: bytes, emb2: bytes):
    print("\n=== Test CompareEmbeddings ===")
    request = speaker_pb2.CompareRequest(embedding1=emb1, embedding2=emb2)
    response = stub.CompareEmbeddings(request)

    print(f"  cosine_similarity  : {response.cosine_similarity:.4f}")
    print(f"  euclidean_distance : {response.euclidean_distance:.4f}")
    print(f"  is_same_speaker    : {response.is_same_speaker}")
    print("  SUCCÈS")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Test client gRPC VoxMind")
    parser.add_argument("--host", default="localhost:50051")
    parser.add_argument("--audio", help="Fichier WAV à utiliser (généré si absent)")
    args = parser.parse_args()

    print(f"Connexion à {args.host}...")

    channel = grpc.insecure_channel(args.host)
    stub = speaker_pb2_grpc.SpeakerRecognitionStub(channel)

    # Préparer l'audio
    if args.audio and os.path.exists(args.audio):
        with open(args.audio, "rb") as f:
            audio_data = f.read()
        print(f"Audio chargé depuis {args.audio} ({len(audio_data)} bytes)")
    else:
        audio_data = generate_test_wav(duration_seconds=3.0)
        print(f"Audio de test généré ({len(audio_data)} bytes)")

    try:
        test_ping(stub)
        emb1 = test_extract_embedding(stub, audio_data)
        if emb1:
            emb2 = test_extract_embedding(stub, audio_data)
            if emb2:
                test_compare_embeddings(stub, emb1, emb2)

        print("\nTous les tests réussis !")
    except grpc.RpcError as e:
        print(f"\nErreur gRPC: {e.code()} - {e.details()}")
        print("Vérifier que le serveur est démarré : python pyannote_server.py --port 50051")
        sys.exit(1)
    finally:
        channel.close()
