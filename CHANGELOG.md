# Changelog

Toutes les modifications notables de ce projet sont documentées dans ce fichier.

## [1.0.0] - 2026-03-22

### Ajouté
- Transcription vocale temps réel via **Whisper.net** (wrapper whisper.cpp)
- Identification des locuteurs via **PyAnnote 4.x** et serveur gRPC
- Mode écoute continu avec `SessionManager` et pipeline audio → embedding → transcription
- Résumés automatiques de session (décisions, actions, moments clés)
- Interface Bridge file-based JSON (compatible Cortana/OpenClaw)
- CLI complète : `start`, `stop`, `status`, `pause`, `resume`, `transcribe`, `enroll`, `list-speakers`, `session`
- Mode interactif (REPL)
- Base de données SQLite via Entity Framework Core 8
- Support multi-plateforme : Linux (PortAudio) / Windows (NAudio)
- Support GPU : CUDA (NVIDIA), ROCm (AMD Linux), CPU
- Docker Compose avec services `voxmind` + `pyannote`
- CI/CD GitHub Actions (build matrix Ubuntu + Windows, coverage Codecov)
- Tests unitaires xUnit : SessionManager, SpeakerIdentification, Configuration
