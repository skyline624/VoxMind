# Changelog

Toutes les modifications notables de ce projet sont documentées dans ce fichier.

## [Unreleased]

### Ajouté
- Moteur TTS **Chatterbox Multilingual ONNX** : voice cloning zero-shot, 23 langues (dont FR), licence MIT, qualité benchmarkée face à ElevenLabs. Exposé comme moteur `chatterbox` (par défaut) via l'endpoint OpenAI-compatible `/v1/audio/speech`. Pipeline 100% C#/ONNX : tokenizer BPE + `speech_encoder` → `language_model` autorégressif à KV-cache → `conditional_decoder` ; variante q4 (CPU temps réel) ou fp16/fp32 (GPU).
- Paramètre `exaggeration` (intensité émotionnelle) et voix de référence configurables par langue ; balises d'expression (`[laughter]`, `[sigh]`, `[whisper]`…).

### Modifié
- Migration du runtime **.NET 8 → .NET 10**.
- `Microsoft.ML.OnnxRuntime` aligné en **1.22.0** (requis par la quantification q4 / MatMulNBits de Chatterbox).

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
