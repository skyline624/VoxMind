# Changelog

Toutes les modifications notables de ce projet sont documentées dans ce fichier.

## [Unreleased]

### Ajouté
- Moteur TTS **Kokoro** (modèle 82M non-autorégressif) servi via **sherpa-onnx** (`org.k2fsa.sherpa.onnx`, `OfflineTts` + `OfflineTtsKokoroModelConfig`). Exposé comme moteur `kokoro` (**par défaut**) sur l'endpoint OpenAI-compatible `/v1/audio/speech`. Sortie WAV 24 kHz, inférence 100% CPU, **RTF ~0.3–0.4 mesuré** (contre > 1 pour un moteur autorégressif).
- Kokoro **multilingue** : une voix féminine prédéfinie par langue (sélection par le champ `language`), modèle `kokoro-multi-lang-v1_0` — `fr` (ff_siwis), `en` (af_heart/en-us), `en-gb` (bf_emma/en-gb-x-rp), `es` (ef_dora), `it` (if_sara), `pt` (pf_dora/pt-br), `hi` (hf_alpha), `ja` (jf_alpha — qualité espeak limitée), `zh` (zf_xiaoxiao/cmn + `lexicon-zh.txt` + dict jieba). Validation des voix espeak (un identifiant inconnu crashait le natif) + lexique/dict configurables par voix.

### Modifié
- Moteur TTS par défaut : **Chatterbox → Kokoro** (`default_engine: "kokoro"`).
- Migration du runtime **.NET 8 → .NET 10**.
- `Microsoft.ML.OnnxRuntime` simplifié en **1.22.0 CPU** uniquement (suppression de la variante conditionnelle `.Gpu` / `UseCuda`).

### Supprimé
- Moteur TTS **Chatterbox** (autorégressif, lent sur CPU) : projet `VoxMind.Chatterbox` (`ChatterboxPipeline`, `ChatterboxTokenizer`), `ChatterboxTtsService`, `ChatterboxLanguageCheckpoint`, config `ChatterboxLanguages`, modèles `models/chatterbox/`.
- Infrastructure **GPU/CUDA** devenue inutile : service Docker `voxmind-tts-gpu`, `Dockerfile.gpu`, `docker/config.gpu.json`, service compose GPU, build conditionnel `UseCuda` + package `Microsoft.ML.OnnxRuntime.Gpu` + symbole `CUDA`.

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
