# VoxMind — Système de Transcription et d'Identification Vocale

## Spécification Technique Complète

> **Nom du projet** : VoxMind  
> **Version** : 1.0.0  
> **Date** : 22 mars 2026  
> **Stack** : C# (.NET 8+) / Python / PyTorch / TorchSharp

---

## ⚠️ IMPORTANT — Documentation Context7

**Avant d'implémenter tout code ML/AI, le développeur DOIT utiliser [Context7](https://context7.com) pour obtenir des exemples de code actualisés et vérifier les dernières API.**

```bash
# Exemples de requêtes Context7 pour ce projet
context7 query "pyannote.audio speaker embedding extraction python 2024"
context7 query "TorchSharp neural network module example 2024"
context7 query "whisper cpp python binding example"
context7 query "grpc csharp async server client example 2024"
```

**Règle** : Toujours vérifier la date des exemples (préférer 2023-2024) et valider la compatibilité avec les versions choisies.

---

## Table des Matières

1. [Objectif du Projet](#1-objectif-du-projet)
2. [Architecture Générale](#2-architecture-générale)
3. [Capture Audio](#3-capture-audio)
4. [Identification des Locuteurs (PyAnnote)](#4-identification-des-locuteurs-pyannote)
5. [Transcription (Whisper)](#5-transcription-whisper)
6. [Mode Écoute Continu](#6-mode-écoute-continu)
7. [Interface Bridge (Communication Externe)](#7-interface-bridge-communication-externe)
8. [Structure des Données](#8-structure-des-données)
9. [CLI (Interface Ligne de Commande)](#9-cli-interface-ligne-de-commande)
10. [Tests](#10-tests)
11. [Modèles et Dépendances](#11-modèles-et-dépendances)
12. [Installation et Configuration](#12-installation-et-configuration)
13. [CI/CD](#13-cicd)
14. [Conteneurisation (Docker)](#14-conteneurisation-docker)
15. [Guide Utilisateur](#15-guide-utilisateur)
16. [Considérations Techniques](#16-considérations-techniques)
17. [Évolutions Futures](#17-évolutions-futures)

---

## 1. Objectif du Projet

Créer un système local de transcription vocale en temps réel avec identification automatique des locuteurs. Le système doit :

- **Fonctionner en arrière-plan** sur un PC (serveur ou desktop)
- **Identifier automatiquement** qui parle parmi une base d'empreintes vocales connues
- **Générer des résumés automatiques** des conversations/réunions
- **Communiquer via interface file-based** (JSON) pour intégration avec assistants externes (Cortana/OpenClaw)
- **Support multi-plateforme** : CPU, CUDA (NVIDIA), ROCm (AMD GPU Linux)

Le système s'appelle **VoxMind** et communique avec Cortana via des fichiers JSON partagés.

---

## 2. Architecture Générale

### 2.1 Stack Technique

| Composant | Technologie | Notes |
|-----------|-------------|-------|
| Langage principal | C# (.NET 8+) | Application principale |
| ML Framework | PyTorch via TorchSharp | Composants ML en C# |
| Interop Python | Python.NET (pythonnet) | Appeler PyAnnote depuis C# |
| Base de données | SQLite via Entity Framework Core | Profils et sessions |
| Audio capture | NAudio (Windows) / PortAudio (Linux) | Multi-sources |
| Tests | xUnit + FluentAssertions | 100% coverage cible |
| Logging | Serilog | Fichiers + console |
| gRPC | Grpc.Net.Client | Communication Python↔C# |

### 2.2 Architecture Composants

```
┌─────────────────────────────────────────────────────────────────────┐
│                        VoxMind (C# .NET 8+)                          │
├─────────────────────────────────────────────────────────────────────┤
│  ┌─────────────┐  ┌──────────────┐  ┌─────────────────────────┐  │
│  │  CLI/UI     │  │ SessionMgr   │  │ OpenClaw Bridge         │  │
│  │  - start    │  │ - Start/Stop │  │ - File polling (500ms)   │  │
│  │  - status   │  │ - Pause/Resume│ │ - JSON command/response  │  │
│  │  - enroll   │  │ - Auto-save   │  │ - Status updates        │  │
│  │  - transcribe│  │              │  │                         │  │
│  └──────┬──────┘  └──────┬───────┘  └───────────┬─────────────┘  │
│         │                │                       │                 │
│  ┌──────▼────────────────▼───────────────────────▼─────────────┐  │
│  │                     CORE SERVICES                              │  │
│  │  ┌──────────────┐  ┌─────────────────┐  ┌────────────────┐  │  │
│  │  │ AudioCapture │  │ TranscriptionSvc│  │ SpeakerRecoSvc │  │  │
│  │  │ - PortAudio  │  │ - TorchSharp    │  │ - PyAnnote gRPC│  │  │
│  │  │ - NAudio     │  │ - Whisper       │  │ - Embeddings   │  │  │
│  │  │ - 16kHz mono │  │ - CPU/CUDA/ROCM │  │ - DB profiles  │  │  │
│  │  └──────────────┘  └─────────────────┘  └────────────────┘  │  │
│  │  ┌──────────────┐  ┌─────────────────┐  ┌────────────────┐  │  │
│  │  │ SessionStore │  │ SummaryGenerator │  │ DatabaseCtx   │  │  │
│  │  │ - JSON files │  │ - Key moments   │  │ - SQLite EF   │  │  │
│  │  │ - Real-time  │  │ - Decisions     │  │ - Migrations  │  │  │
│  │  │ - Segments   │  │ - Actions       │  │               │  │  │
│  │  └──────────────┘  └─────────────────┘  └────────────────┘  │  │
│  └──────────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────────┘
                              │ gRPC (localhost:50051)
                    ┌─────────▼─────────┐
                    │  Python Services  │
                    │  ┌─────────────┐  │
                    │  │ PyAnnote    │  │
                    │  │ - Diarization│  │
                    │  │ - Embedding │  │
                    │  └─────────────┘  │
                    │  ┌─────────────┐  │
                    │  │ Whisper.cpp │  │
                    │  │ (optional)  │  │
                    │  └─────────────┘  │
                    └──────────────────┘
```

### 2.3 Structure de la Solution

```
VoxMind/
├── src/
│   ├── VoxMind.Core/                    # Bibliothèque principale
│   │   ├── Audio/
│   │   │   ├── IAudioCapture.cs
│   │   │   ├── PortAudioCapture.cs      # Linux
│   │   │   ├── NAudioCapture.cs         # Windows
│   │   │   ├── AudioSource.cs
│   │   │   ├── AudioChunk.cs
│   │   │   └── AudioDeviceInfo.cs
│   │   ├── Transcription/
│   │   │   ├── ITranscriptionService.cs
│   │   │   ├── WhisperService.cs        # TorchSharp
│   │   │   ├── TranscriptionResult.cs
│   │   │   ├── TranscriptionSegment.cs
│   │   │   └── ComputeBackend.cs
│   │   ├── SpeakerRecognition/
│   │   │   ├── ISpeakerIdentificationService.cs
│   │   │   ├── SpeakerProfile.cs
│   │   │   ├── SpeakerEmbedding.cs
│   │   │   ├── SpeakerIdentificationResult.cs
│   │   │   └── PyAnnoteGrpcClient.cs
│   │   ├── Session/
│   │   │   ├── ISessionManager.cs
│   │   │   ├── ListeningSession.cs
│   │   │   ├── SessionSegment.cs
│   │   │   ├── SessionSummary.cs
│   │   │   └── SummaryGenerator.cs
│   │   ├── Database/
│   │   │   ├── VoxMindDbContext.cs
│   │   │   ├── SpeakerProfileEntity.cs
│   │   │   ├── SpeakerEmbeddingEntity.cs
│   │   │   ├── ListeningSessionEntity.cs
│   │   │   └── SessionSegmentEntity.cs
│   │   ├── Bridge/
│   │   │   ├── IExternalBridge.cs
│   │   │   ├── FileBridge.cs
│   │   │   ├── Command.cs
│   │   │   └── SystemStatus.cs
│   │   ├── Configuration/
│   │   │   ├── AppConfiguration.cs
│   │   │   └── ConfigurationLoader.cs
│   │   └── Extensions/
│   │       └── ServiceCollectionExtensions.cs
│   ├── VoxMind.CLI/                     # Interface ligne de commande
│   │   ├── Program.cs
│   │   ├── Commands/
│   │   │   ├── StartCommand.cs
│   │   │   ├── StopCommand.cs
│   │   │   ├── StatusCommand.cs
│   │   │   ├── EnrollCommand.cs
│   │   │   ├── TranscribeCommand.cs
│   │   │   ├── ListSpeakersCommand.cs
│   │   │   └── SessionCommands.cs
│   │   ├── Interactive/
│   │   │   ├── InteractiveMode.cs
│   │   │   └── ColorConsole.cs
│   │   └── VoxMind.CLI.csproj
│   └── VoxMind.Tests/                   # Tests
│       ├── Unit/
│       ├── Integration/
│       └── Performance/
├── python_services/                     # Services Python (requis pour PyAnnote)
│   ├── pyannote_server.py               # Serveur gRPC pour PyAnnote
│   ├── whisper_server.py                # Serveur Whisper (si utilisé)
│   ├── requirements.txt
│   ├── Dockerfile
│   └── download_models.py
├── voice_data/                          # Données runtime
│   ├── profiles/
│   ├── sessions/
│   ├── shared/
│   ├── cache/
│   └── logs/
├── docs/
│   ├── API.md
│   └── ARCHITECTURE.md
├── .github/
│   └── workflows/
│       ├── build.yml
│       └── tests.yml
├── VoxMind.sln
├── README.md
├── LICENSE
└── CHANGELOG.md
```

---

## 3. Capture Audio

### 3.1 Sources Audio Supportées

| Source | Description | Plateforme | Implémentation |
|--------|-------------|------------|----------------|
| Microphone principal | Entrée microphone système | Toutes | PortAudio/NAudio |
| Audio système | Sortie audio du PC (apps, appels) | Windows (WASAPI), Linux (PulseAudio) | PortAudio |
| Audio HDMI/USB | Sources audio externes | Toutes | PortAudio |
| Multiple simultanées | Combinaison de sources | Configurable | Mixage |

### 3.2 Spécifications Techniques

```csharp
// Fichiers : VoxMind.Core/Audio/AudioConfiguration.cs

public enum AudioSourceType
{
    Microphone,
    SystemAudio,
    HDMI,
    USB
}

public class AudioConfiguration
{
    // Format audio (optimal pour whisper: 16kHz mono)
    public int SampleRate { get; set; } = 16000;
    public int BitDepth { get; set; } = 16;
    public int Channels { get; set; } = 1;  // Mono
    
    // Buffer et latence
    public int ChunkDurationMs { get; set; } = 100;   // Latence de traitement
    public int BufferSize { get; set; } = 1600;       // samples par chunk
    
    // Sources activées
    public List<AudioSourceType> EnabledSources { get; set; } = new();
    
    // Mode capture
    public bool MixSources { get; set; } = true;  // Mélanger ou separate
}
```

### 3.3 Interface de Capture

```csharp
// Fichiers : VoxMind.Core/Audio/IAudioCapture.cs

public interface IAudioCapture : IDisposable
{
    /// <summary>Énumère les sources audio disponibles sur le système</summary>
    Task<IReadOnlyList<AudioDeviceInfo>> GetAvailableSourcesAsync();
    
    /// <summary>Démarre la capture audio</summary>
    Task StartCaptureAsync(AudioConfiguration config, CancellationToken ct = default);
    
    /// <summary>Arrête la capture audio</summary>
    Task StopCaptureAsync();
    
    /// <summary>Événement déclenché pour chaque chunk audio capté</summary>
    event EventHandler<AudioChunkEventArgs>? AudioChunkReceived;
    
    /// <summary>Indique si la capture est active</summary>
    bool IsCapturing { get; }
    
    /// <summary>Configuration actuelle</summary>
    AudioConfiguration CurrentConfig { get; }
}

public class AudioDeviceInfo
{
    public int DeviceIndex { get; set; }
    public string Name { get; set; }
    public AudioSourceType Type { get; set; }
    public int MaxChannels { get; set; }
    public int DefaultSampleRate { get; set; }
    public bool IsDefault { get; set; }
}

public class AudioChunk
{
    public byte[] RawData { get; }
    public AudioSourceType Source { get; }
    public TimeSpan Timestamp { get; }
    public int SampleRate { get; }
    public TimeSpan Duration { get; }
    public int SamplesCount { get; }
}
```

### 3.4 Gestion des Erreurs et Robustesse

| Situation | Comportement |
|-----------|--------------|
| Déconnexion microphone | Reconnexion automatique pendant 30s, puis signalement ERROR_AUDIO_DISCONNECTED |
| Source unavailable | Skip de la source, continue avec les autres si MixSources=false |
| Buffer overflow | Drop des chunks les plus anciens, log warning |
| Bruit de fond important | Pas de filtrage automatique (laiss&é à l'utilisateur) |
| Silence prolongé | Continu mais pas de transcription (VAD optionnel) |
| plusieurs locuteurs simultanés | Capture complète, PyAnnote gère la diarization |

### 3.5 Formats Audio Supportés

| Format | Extension | Support | Notes |
|--------|-----------|---------|-------|
| WAV PCM | .wav | ✅ Required | 16kHz mono 16bit |
| MP3 | .mp3 | ✅ | Conversion automatique |
| OGG | .ogg | ✅ | Via FFmpeg ou libsamplerate |
| FLAC | .flac | ✅ | Sans perte |
| Speex | .spx | ❌ | Non supporté |

**Note** : Whisper fonctionne optimalement avec du WAV PCM 16kHz mono. Les autres formats sont convertis automatiquement.

---

## 4. Identification des Locuteurs (PyAnnote)

### 4.1 Architecture PyAnnote

PyAnnote ne dispose pas de port C# natif. L'architecture retenue utilise **gRPC** pour la communication :

```
┌─────────────────┐         gRPC          ┌──────────────────┐
│   VoxMind C#    │ ◄──────────────────► │  Python Service   │
│                 │  ExtractEmbedding    │                  │
│ PyAnnoteGrpc    │ ───────────────────► │  pyannote_server  │
│ Client          │                      │                  │
│                 │ ◄─────────────────── │                  │
│                 │  EmbeddingResponse   │                  │
└─────────────────┘                      └──────────────────┘
```

### 4.2 Proto Definition

```protobuf
// python_services/protos/speaker.proto

syntax = "proto3";

package voxmind;

service SpeakerRecognition {
  // Extrait un embedding vocal à partir d'audio
  rpc ExtractEmbedding(AudioData) returns (EmbeddingResponse);
  
  // Compare deux embeddings
  rpc CompareEmbeddings(CompareRequest) returns (CompareResponse);
  
  // Health check
  rpc Ping(Empty) returns (Pong);
}

message AudioData {
  bytes audio_data = 1;      // WAV PCM 16kHz mono
  float sample_rate = 2;     // Devrait être 16000
  int32 duration_ms = 3;      // Durée de l'audio en ms
}

message EmbeddingResponse {
  bool success = 1;
  bytes embedding = 2;        // Vector float32, dimension 512 ou 1024
  float duration_used = 3;   // Durée de l'audio utilisé pour l'embedding
  string error = 4;
}

message CompareRequest {
  bytes embedding1 = 1;
  bytes embedding2 = 2;
}

message CompareResponse {
  float cosine_similarity = 1;
  float euclidean_distance = 2;
  bool is_same_speaker = 3;   // Basé sur le seuil configuré
}

message Empty {}

message Pong {
  bool alive = 1;
  string version = 2;
}
```

### 4.3 Modèle de Données

```csharp
// Fichier : VoxMind.Core/SpeakerRecognition/SpeakerProfile.cs

public class SpeakerProfile
{
    public Guid Id { get; set; }
    public string Name { get; set; }                      // Nom assigné
    public List<string> Aliases { get; set; } = new();    // Noms alternatifs
    public List<SpeakerEmbedding> Embeddings { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public int DetectionCount { get; set; }
    public bool IsActive { get; set; } = true;
    public string Notes { get; set; }                     // Notes optionnelles
}

public class SpeakerEmbedding
{
    public Guid Id { get; set; }
    public Guid ProfileId { get; set; }
    public string FilePath { get; set; }                   // Chemin vers le .pt
    public DateTime CapturedAt { get; set; }
    public float InitialConfidence { get; set; }            // Score à l'enregistrement
    public int AudioDurationSeconds { get; set; }
    public float[] EmbeddingVector { get; set; }            // Cache en mémoire
}

public class SpeakerIdentificationResult
{
    public bool IsIdentified { get; }
    public Guid? ProfileId { get; }
    public string? SpeakerName { get; }
    public float Confidence { get; }
    public float Threshold { get; }
    public bool IsNewSpeaker => !IsIdentified && Confidence >= Threshold;
}
```

### 4.4 Service d'Identification

```csharp
// Fichier : VoxMind.Core/SpeakerRecognition/ISpeakerIdentificationService.cs

public interface ISpeakerIdentificationService : IDisposable
{
    /// <summary>Enroller un nouveau locuteur avec une empreinte vocale</summary>
    Task<SpeakerProfile> EnrollSpeakerAsync(string name, float[] embedding, float initialConfidence);
    
    /// <summary>Ajouter une nouvelle empreinte à un profil existant</summary>
    Task AddEmbeddingToProfileAsync(Guid profileId, float[] embedding, float confidence);
    
    /// <summary>Identifier un locuteur à partir d'une empreinte</summary>
    Task<SpeakerIdentificationResult> IdentifyAsync(float[] embedding);
    
    /// <summary>Obtenir un profil par ID</summary>
    Task<SpeakerProfile?> GetProfileAsync(Guid profileId);
    
    /// <summary>Lister tous les profils</summary>
    Task<IReadOnlyList<SpeakerProfile>> GetAllProfilesAsync();
    
    /// <summary>Fusionner deux profils (même personne)</summary>
    Task MergeProfilesAsync(Guid targetProfileId, Guid sourceProfileId);
    
    /// <summary>Renommer un profil</summary>
    Task RenameProfileAsync(Guid profileId, string newName);
    
    /// <summary>Supprimer un profil et ses embeddings</summary>
    Task DeleteProfileAsync(Guid profileId);
    
    /// <summary>Fusionner deux locuteurs identifiés comme étant la même personne</summary>
    Task LinkSpeakersAsync(Guid knownProfileId, Guid unknownProfileId);
    
    /// <summary>Mettre à jour la date de dernière détection</summary>
    Task UpdateLastSeenAsync(Guid profileId);
    
    /// <summary>Vérifier la connexion au service PyAnnote</summary>
    Task<bool> CheckHealthAsync();
}
```

### 4.5 Schéma Base de Données (SQLite)

```sql
-- Fichier : VoxMind.Core/Database/VoxMindDbContext.cs (OnModelCreating)

CREATE TABLE SpeakerProfiles (
    Id TEXT PRIMARY KEY,
    Name TEXT NOT NULL,
    Aliases TEXT,                     -- JSON array
    CreatedAt TEXT NOT NULL,
    LastSeenAt TEXT,
    DetectionCount INTEGER DEFAULT 0,
    IsActive INTEGER DEFAULT 1,
    Notes TEXT
);

CREATE TABLE SpeakerEmbeddings (
    Id TEXT PRIMARY KEY,
    ProfileId TEXT NOT NULL,
    FilePath TEXT NOT NULL,
    CapturedAt TEXT NOT NULL,
    InitialConfidence REAL,
    AudioDurationSeconds INTEGER,
    FOREIGN KEY (ProfileId) REFERENCES SpeakerProfiles(Id) ON DELETE CASCADE
);

CREATE TABLE ListeningSessions (
    Id TEXT PRIMARY KEY,
    Name TEXT,
    StartedAt TEXT NOT NULL,
    EndedAt TEXT,
    Participants TEXT,                -- JSON array de speaker IDs
    Config TEXT,                      -- JSON de la configuration
    Status TEXT DEFAULT 'active'       -- active, paused, completed
);

CREATE TABLE SessionSegments (
    Id TEXT PRIMARY KEY,
    SessionId TEXT NOT NULL,
    SpeakerId TEXT,                   -- NULL si inconnu
    StartTime REAL NOT NULL,          -- secondes
    EndTime REAL NOT NULL,
    Transcript TEXT NOT NULL,
    Confidence REAL,
    FOREIGN KEY (SessionId) REFERENCES ListeningSessions(Id) ON DELETE CASCADE,
    FOREIGN KEY (SpeakerId) REFERENCES SpeakerProfiles(Id)
);

CREATE TABLE SessionSummaries (
    Id TEXT PRIMARY KEY,
    SessionId TEXT NOT NULL UNIQUE,
    GeneratedAt TEXT NOT NULL,
    FullTranscript TEXT,
    KeyMoments TEXT,                 -- JSON array
    Decisions TEXT,                   -- JSON array
    ActionItems TEXT,                 -- JSON array
    GeneratedSummary TEXT,
    FOREIGN KEY (SessionId) REFERENCES ListeningSessions(Id) ON DELETE CASCADE
);

-- Index pour performances
CREATE INDEX idx_embeddings_profile ON SpeakerEmbeddings(ProfileId);
CREATE INDEX idx_segments_session ON SessionSegments(SessionId);
CREATE INDEX idx_profiles_lastseen ON SpeakerProfiles(LastSeenAt);
CREATE INDEX idx_sessions_started ON ListeningSessions(StartedAt);
```

### 4.6 Commandes de Gestion (via Bridge)

| Commande | Paramètres | Description |
|----------|------------|-------------|
| `SAVE_SPEAKER` | `speaker_id`, `name` | Sauvegarder un locuteur inconnu sous un nom |
| `MERGE_SPEAKERS` | `speaker_ids[]` | Fusionner plusieurs profils en un |
| `LINK_SPEAKERS` | `known_id`, `unknown_id` | Lier un inconnu à un profil connu |
| `RENAME_SPEAKER` | `speaker_id`, `name` | Renommer un profil |
| `DELETE_SPEAKER` | `speaker_id` | Supprimer un profil |
| `LIST_SPEAKERS` | - | Lister tous les profils |
| `GET_SPEAKER` | `speaker_id` | Obtenir les détails d'un profil |
| `IMPORT_SPEAKER` | `profile_json` | Importer un profil depuis JSON |
| `EXPORT_SPEAKER` | `speaker_id` | Exporter un profil en JSON |

---

## 5. Transcription (Whisper)

### 5.1 Service de Transcription

```csharp
// Fichier : VoxMind.Core/Transcription/ITranscriptionService.cs

public enum ModelSize { Tiny, Base, Small, Medium, Large }
public enum ComputeBackend { CPU, CUDA, ROCm, MPS, Auto }

public interface ITranscriptionService : IDisposable
{
    /// <summary>Transcrire un fichier audio complet</summary>
    Task<TranscriptionResult> TranscribeFileAsync(string filePath, CancellationToken ct = default);
    
    /// <summary>Transcrire un chunk audio (temps réel)</summary>
    Task<TranscriptionResult> TranscribeChunkAsync(byte[] audioData, CancellationToken ct = default);
    
    /// <summary>Détecter la langue d'un audio</summary>
    Task<string> DetectLanguageAsync(byte[] audioData);
    
    /// <summary>Statut et info du modèle</summary>
    ModelInfo Info { get; }
    
    /// <summary>Charger un nouveau modèle</summary>
    Task LoadModelAsync(ModelSize size, ComputeBackend backend = ComputeBackend.Auto);
}

public class ModelInfo
{
    public string ModelName { get; }
    public ModelSize Size { get; }
    public ComputeBackend Backend { get; }
    public bool IsLoaded { get; }
    public long MemoryUsageBytes { get; }
}

public class TranscriptionResult
{
    public string Text { get; set; }
    public string Language { get; set; }
    public float Confidence { get; set; }
    public TimeSpan Duration { get; set; }
    public List<TranscriptionSegment> Segments { get; set; } = new();
    public DateTime ProcessedAt { get; set; }
}

public class TranscriptionSegment
{
    public int Id { get; set; }
    public TimeSpan Start { get; set; }
    public TimeSpan End { get; set; }
    public string Text { get; set; }
    public float Confidence { get; set; }
    public List<WordTimestamp> Words { get; set; } = new();
}

public class WordTimestamp
{
    public string Word { get; set; }
    public TimeSpan Start { get; set; }
    public TimeSpan End { get; set; }
}
```

### 5.2 Modèles Whisper

| Modèle | Paramètres | VRAM | Relative Speed | Use Case |
|--------|------------|------|----------------|----------|
| Tiny | 39M | ~1GB | 10x | Real-time, CPU fallback |
| Base | 74M | ~1GB | 6x | Real-timeRecommended |
| Small | 244M | ~2GB | 3x | Balance qualité/vitesse |
| Medium | 769M | ~5GB | 1x | Précision élevée |
| Large | 1550M | ~10GB | 0.5x | Maximum précision |

**Recommandation** : Base ou Small pour temps réel, Medium/Large pour post-traitement.

### 5.3 Backend Compute

```csharp
// Fichier : VoxMind.Core/Transcription/ComputeBackend.cs

public static class ComputeBackendDetector
{
    public static ComputeBackend DetectBestAvailable()
    {
        // CUDA (NVIDIA)
        if (torch.cuda.is_available())
            return ComputeBackend.CUDA;
        
        // MPS (Apple Silicon)
        if (torch.backends.mps.is_available())
            return ComputeBackend.MPS;
        
        // ROCm (AMD GPU Linux)
        if (IsRocmAvailable())
            return ComputeBackend.ROCm;
        
        return ComputeBackend.CPU;
    }
    
    public static string GetDeviceString(ComputeBackend backend)
    {
        return backend switch
        {
            ComputeBackend.CUDA => "cuda:0",
            ComputeBackend.MPS => "mps:0",
            ComputeBackend.ROCm => "hip:0",
            ComputeBackend.CPU => "cpu",
            ComputeBackend.Auto => DetectBestAvailable().GetDeviceString(),
            _ => "cpu"
        };
    }
}
```

### 5.4 Pipeline Transcription + Identification

```csharp
// Fichier : VoxMind.Core/Session/SessionManager.cs

public class DiarizedTranscription
{
    public TimeSpan Start { get; set; }
    public TimeSpan End { get; set; }
    public Guid? SpeakerId { get; set; }
    public string? SpeakerName { get; set; }
    public string Text { get; set; }
    public float Confidence { get; set; }
}

public async Task<DiarizedTranscription> ProcessAudioChunkAsync(
    byte[] audioChunk,
    CancellationToken ct)
{
    // Étape 1 : Extraction de l'embedding vocal
    var embeddingResponse = await _pyAnnoteClient.ExtractEmbeddingAsync(audioChunk);
    if (!embeddingResponse.Success)
    {
        _logger.LogWarning("PyAnnote embedding failed: {Error}", embeddingResponse.Error);
    }
    
    // Étape 2 : Identification du locuteur
    SpeakerIdentificationResult? identification = null;
    if (embeddingResponse.Success && embeddingResponse.Embedding.Length > 0)
    {
        identification = await _speakerService.IdentifyAsync(embeddingResponse.Embedding);
        
        if (identification.IsNewSpeaker)
        {
            _logger.LogInformation("New speaker detected with confidence {Confidence}", 
                identification.Confidence);
        }
    }
    
    // Étape 3 : Transcription
    var transcription = await _transcriptionService.TranscribeChunkAsync(audioChunk, ct);
    
    // Étape 4 : Combinaison des résultats
    return new DiarizedTranscription
    {
        Start = transcription.Segments.FirstOrDefault()?.Start ?? TimeSpan.Zero,
        End = transcription.Segments.LastOrDefault()?.End ?? TimeSpan.Zero,
        SpeakerId = identification?.ProfileId,
        SpeakerName = identification?.SpeakerName,
        Text = transcription.Text,
        Confidence = transcription.Confidence
    };
}
```

---

## 6. Mode Écoute Continu

### 6.1 Gestionnaire de Session

```csharp
// Fichier : VoxMind.Core/Session/ISessionManager.cs

public enum SessionStatus { Idle, Listening, Paused }

public interface ISessionManager : IDisposable
{
    /// <summary>Démarrer une session d'écoute</summary>
    Task<ListeningSession> StartSessionAsync(string? name = null);
    
    /// <summary>Arrêter la session (SEUL moyen d'arrêt)</summary>
    Task<ListeningSession> StopSessionAsync();
    
    /// <summary>Mettre en pause (optionnel)</summary>
    Task PauseSessionAsync();
    
    /// <summary>Reprendre après pause</summary>
    Task ResumeSessionAsync();
    
    /// <summary>Statut actuel</summary>
    SessionStatus Status { get; }
    
    /// <summary>Session active</summary>
    ListeningSession? CurrentSession { get; }
    
    /// <summary>Événement : nouveau segment traité</summary>
    event EventHandler<SegmentProcessedEventArgs>? SegmentProcessed;
    
    /// <summary>Événement : session terminée</summary>
    event EventHandler<SessionEndedEventArgs>? SessionEnded;
}

public class ListeningSession
{
    public Guid Id { get; }
    public string? Name { get; }
    public DateTime StartedAt { get; }
    public DateTime? EndedAt { get; private set; }
    public TimeSpan Duration => (EndedAt ?? DateTime.UtcNow) - StartedAt;
    public SessionStatus Status { get; set; }
    public AudioConfiguration Config { get; set; }
    public List<Guid> ParticipantIds { get; set; } = new();
    public int SegmentCount { get; internal set; }
}

public class SegmentProcessedEventArgs : EventArgs
{
    public Guid SessionId { get; }
    public DiarizedTranscription Segment { get; }
    public TimeSpan Elapsed { get; }
    public int TotalSegments { get; }
}

public class SessionEndedEventArgs : EventArgs
{
    public ListeningSession Session { get; }
    public TimeSpan Duration { get; }
    public int TotalSegments { get; }
    public SessionSummary? Summary { get; }
}
```

### 6.2 Comportement du Mode Écoute

```
┌─────────────────────────────────────────────────────────────────┐
│                         MODE ÉCOUTE                             │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│  START ──────► LISTENING ───────────────► STOP ────► COMPLETED   │
│                  │                            (SEUL point        │
│                  │                             d'arrêt)         │
│                  │                                                 │
│                  ├──────► PAUSED ◄───────► RESUMED              │
│                  │        (optionnel)                            │
│                                                                  │
│  Pendant LISTENING :                                            │
│  - Capture audio continue (buffer 100ms)                        │
│  - Transcription temps réel                                      │
│  - Identification locuteurs                                      │
│  - Sauvegarde segments JSON (temps réel)                        │
│  - Résumé intermédiaire (toutes les 5 min)                       │
│  - AUCUNE intervention automatique                               │
│                                                                  │
│  Désactivation :                                                │
│  - Command STOP uniquement                                       │
│  - Pas de timeout automatique                                   │
│  - Pas de détection silence                                     │
│  - Pas d'inactivité                                             │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

### 6.3 Résumé de Session

```csharp
// Fichier : VoxMind.Core/Session/SessionSummary.cs

public class SessionSummary
{
    public Guid SessionId { get; set; }
    public string Name { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime EndedAt { get; set; }
    public TimeSpan Duration { get; set; }
    
    public List<ParticipantSummary> Participants { get; set; } = new();
    public List<string> KeyMoments { get; set; } = new();
    public List<string> Decisions { get; set; } = new();
    public List<string> ActionItems { get; set; } = new();
    
    public string FullTranscript { get; set; }
    public string GeneratedSummary { get; set; }
    
    public DateTime GeneratedAt { get; set; }
}

public class ParticipantSummary
{
    public Guid SpeakerId { get; set; }
    public string Name { get; set; }
    public TimeSpan SpeakingTime { get; set; }
    public int UtteranceCount { get; set; }
    public float AverageConfidence { get; set; }
    public float PercentageOfSession { get; set; }
}
```

### 6.4 Génération Automatique du Résumé

Le résumé est généré via analyse textuelle des transcriptions :

```csharp
// Fichier : VoxMind.Core/Session/SummaryGenerator.cs

public interface ISummaryGenerator
{
    Task<SessionSummary> GenerateAsync(ListeningSession session, CancellationToken ct = default);
}

public class SummaryGenerator : ISummaryGenerator
{
    public Task<SessionSummary> GenerateAsync(ListeningSession session, CancellationToken ct = default)
    {
        var fullTranscript = GetFullTranscript(session);
        
        // Extraction des moments clés
        var keyMoments = ExtractKeyMoments(fullTranscript);
        
        // Identification des décisions
        var decisions = ExtractDecisions(fullTranscript);
        
        // Extraction des actions
        var actions = ExtractActionItems(fullTranscript);
        
        // Synthèse narrative
        var narrativeSummary = GenerateNarrative(
            session.Duration,
            session.ParticipantIds.Count,
            keyMoments,
            decisions
        );
        
        return new SessionSummary
        {
            SessionId = session.Id,
            Name = session.Name,
            StartedAt = session.StartedAt,
            EndedAt = session.EndedAt ?? DateTime.UtcNow,
            Duration = session.Duration,
            Participants = GetParticipantSummaries(session),
            KeyMoments = keyMoments,
            Decisions = decisions,
            ActionItems = actions,
            FullTranscript = fullTranscript,
            GeneratedSummary = narrativeSummary,
            GeneratedAt = DateTime.UtcNow
        };
    }
    
    // Patterns pour extraction
    private static readonly string[] DecisionPatterns = {
        "on va faire", "c'est décidé", "je suis d'accord",
        "nous allons", "donc on", "il est décidé"
    };
    
    private static readonly string[] ActionPatterns = {
        "il faut", "je vais", "tu dois", "nous devons",
        "je m'occupe", "tu geres", "je reprends"
    };
}
```

### 6.5 Signaux Système (Arrêt Propre)

```csharp
// Fichier : VoxMind.CLI/Program.cs

public static class SignalHandler
{
    public static CancellationTokenSource Setup CancellationTokenSource { get; private set; }
    
    public static void Setup()
    {
        CancellationTokenSource = new CancellationTokenSource();
        
        // SIGTERM (docker, systemctl stop)
        Console.CancelKeyPress += (sender, args) =>
        {
            args.Cancel = true;
            CancellationTokenSource.Cancel();
            Console.WriteLine("\n⚠ SIGINT received, stopping gracefully...");
        };
        
        AppDomain.CurrentDomain.ProcessExit += (sender, args) =>
        {
            if (!CancellationTokenSource.IsCancellationRequested)
            {
                CancellationTokenSource.Cancel();
            }
        };
    }
}
```

---

## 7. Interface Bridge (Communication Externe)

### 7.1 Communication File-Based

```
voice_data/shared/
├── commands_to_voxmind.json      # Commandes → VoxMind
├── status_from_voxmind.json      # Statut VoxMind →
├── pending_audio.json            # Audio en attente (optionnel)
├── transcription_output.json     # Résultats de transcription
└── session_updates.json          # Mises à jour de session
```

### 7.2 Commandes Externes (JSON)

```json
{
  "command_id": "550e8400-e29b-41d4-a716-446655440000",
  "command": "START_LISTENING",
  "parameters": {
    "session_name": "reunion_marjorie",
    "sources": ["micro", "system"]
  },
  "timestamp": "2026-03-22T14:30:00Z"
}
```

| Commande | Paramètres | Description |
|----------|------------|-------------|
| `START_LISTENING` | `session_name`, `sources[]` | Démarrer une session |
| `STOP_LISTENING` | - | Arrêter la session (OBLIGATOIRE) |
| `PAUSE_LISTENING` | - | Mettre en pause |
| `RESUME_LISTENING` | - | Reprendre |
| `GET_STATUS` | - | Statut actuel |
| `GET_SESSION` | `session_id` | Détails d'une session |
| `LIST_SESSIONS` | `from_date`, `to_date` | Lister les sessions |
| `GET_SUMMARY` | `session_id` | Résumé d'une session |
| `GET_LAST_SUMMARY` | - | Résumé de la session en cours |
| `TRANSCRIBE_FILE` | `file_path` | Transcrire un fichier audio |
| `SAVE_SPEAKER` | `speaker_id`, `name` | Sauvegarder un locuteur |
| `MERGE_SPEAKERS` | `speaker_ids[]` | Fusionner des profils |
| `LINK_SPEAKERS` | `known_id`, `unknown_id` | Lier inconnu à connu |
| `RENAME_SPEAKER` | `speaker_id`, `name` | Renommer |
| `DELETE_SPEAKER` | `speaker_id` | Supprimer |
| `LIST_SPEAKERS` | - | Lister les profils |
| `GET_SPEAKER` | `speaker_id` | Détails d'un profil |
| `SHUTDOWN` | - | Arrêter VoxMind complètement |

### 7.3 Réponses du Système (JSON)

```json
{
  "response_id": "uuid",
  "command_id": "uuid",
  "status": "success",
  "data": {
    "session_id": "uuid",
    "is_listening": true,
    "started_at": "2026-03-22T14:30:00Z",
    "participants": ["alex", "inconnu_001"]
  },
  "error": null,
  "timestamp": "2026-03-22T14:30:05Z"
}
```

### 7.4 Statut du Système (JSON)

```json
{
  "status": "listening",
  "uptime_seconds": 847,
  "current_session": {
    "id": "uuid",
    "name": "reunion_marjorie",
    "started_at": "2026-03-22T14:30:00Z",
    "elapsed_seconds": 847,
    "segments_processed": 23,
    "participants": ["alex"]
  },
  "compute": {
    "backend": "CUDA",
    "gpu_name": "NVIDIA RTX 3080",
    "gpu_memory_used_mb": 2048,
    "gpu_memory_total_mb": 10240
  },
  "models": {
    "whisper": { "size": "base", "loaded": true },
    "pyannote": { "loaded": true }
  },
  "last_activity": "2026-03-22T14:44:05Z",
  "voxmind_version": "1.0.0"
}
```

### 7.5 Codes d'Erreur

| Code | Constante | Description |
|------|-----------|-------------|
| 0 | `SUCCESS` | Succès |
| 1 | `ERROR_GENERAL` | Erreur générale |
| 2 | `ERROR_AUDIO_DEVICE` | Erreur matériel audio |
| 3 | `ERROR_AUDIO_DISCONNECTED` | Périphérique déconnecté |
| 4 | `ERROR_ML_MODEL` | Erreur modèle ML |
| 5 | `ERROR_TRANSCRIPTION` | Erreur transcription |
| 6 | `ERROR_SPEAKER_ID` | Erreur identification locuteur |
| 7 | `ERROR_DATABASE` | Erreur base de données |
| 8 | `ERROR_SESSION` | Erreur de session |
| 9 | `ERROR_BRIDGE` | Erreur communication bridge |
| 10 | `ERROR_PYANNOTE` | Erreur service PyAnnote |
| 99 | `ERROR_UNKNOWN` | Erreur inconnue |

---

## 8. Structure des Données

### 8.1 Arborescence Complète

```
/home/pc/voice_data/
├── profiles/
│   ├── database.sqlite              # Base SQLite (EF Core)
│   ├── database.sqlite-wal         # WAL journal
│   ├── database.sqlite-shm        # Shared memory
│   └── backups/
│       ├── 2026-03-20_backup.db
│       └── 2026-03-21_backup.db
├── embeddings/                      # Fichiers embeddings PyAnnote
│   ├── {uuid}_emb_001.pt
│   ├── {uuid}_emb_002.pt
│   └── ...
├── sessions/                        # Sessions d'écoute
│   ├── {session_id}/
│   │   ├── session.json            # Métadonnées
│   │   ├── segments.json          # Segments transcrits
│   │   ├── summary.json            # Résumé généré
│   │   └── audio_cache.wav         # Audio original (si sauvegardé)
│   ├── 2026-03-20_14-30_reunion_marjorie.json
│   └── 2026-03-21_09-00_appel_pierre.json
├── shared/                          # Interface Bridge
│   ├── commands_to_voxmind.json
│   ├── status_from_voxmind.json
│   ├── pending_audio.json
│   ├── transcription_output.json
│   └── session_updates.json
├── cache/                           # Cache des modèles ML
│   └── whisper/
│       ├── tiny.pt
│       ├── base.pt
│       ├── small.pt
│       └── model.json              # Métadonnées du modèle
├── logs/
│   ├── voxmind_2026-03-22.log
│   ├── voxmind_2026-03-23.log
│   └── sessions/
│       └── {session_id}.log
└── config/
    ├── config.json                 # Configuration principale
    └── config.schema.json         # JSON schema pour validation
```

### 8.2 Format d'une Session (JSON)

```json
{
  "id": "550e8400-e29b-41d4-a716-446655440000",
  "name": "reunion_marjorie",
  "started_at": "2026-03-22T14:30:00Z",
  "ended_at": "2026-03-22T15:15:00Z",
  "duration_seconds": 2700,
  "config": {
    "sources": ["micro", "system"],
    "sample_rate": 16000,
    "chunk_duration_ms": 100
  },
  "status": "completed",
  "participants": [
    {
      "speaker_id": "uuid-alex",
      "name": "Alex",
      "speaking_time_seconds": 892,
      "utterance_count": 34,
      "percentage": 33.0
    },
    {
      "speaker_id": "uuid-marjorie",
      "name": "Marjorie",
      "speaking_time_seconds": 1205,
      "utterance_count": 47,
      "percentage": 44.6
    },
    {
      "speaker_id": null,
      "name": "Inconnu",
      "speaking_time_seconds": 603,
      "utterance_count": 21,
      "percentage": 22.3
    }
  ],
  "segments": [
    {
      "id": "uuid-seg-001",
      "speaker_id": "uuid-alex",
      "speaker_name": "Alex",
      "start_seconds": 0.5,
      "end_seconds": 5.2,
      "text": "Bonjour Marjorie, on commence la réunion.",
      "confidence": 0.95
    },
    {
      "id": "uuid-seg-002",
      "speaker_id": "uuid-marjorie",
      "speaker_name": "Marjorie",
      "start_seconds": 5.8,
      "end_seconds": 12.3,
      "text": "Salut Alex ! Oui, alors j'ai avancé sur le site Madam Artworks...",
      "confidence": 0.89
    }
  ],
  "key_moments": [
    "14:32 - Début de la discussion sur le projet Madam Artworks",
    "14:45 - Marjorie mentionne un problème avec le client Terres d'empreintes",
    "14:58 - Discussion sur le devis et les délais",
    "15:10 - Décision de reporter le rendu au 30 mars"
  ],
  "decisions": [
    "Report du rendu Madam Artworks au 30 mars 2026",
    "Appel avec le client Terres d'empreintes la semaine prochaine",
    "Augmentation du budget Phonurgia à 6000€"
  ],
  "action_items": [
    { "assignee": "Alex", "action": "Relancer Marjorie sur le devis corrigé", "deadline": "2026-03-24" },
    { "assignee": "Marjorie", "action": "Envoyer les modifications du site Phonurgia", "deadline": "2026-03-25" },
    { "assignee": "Tous", "action": "Préparer l'appel client Terres d'empreintes", "deadline": "2026-03-28" }
  ],
  "summary": "Réunion de 45 minutes entre Alex et Marjorie concernant l'avancement des projets web. Marjorie a avancé sur Madam Artworks mais rencontre des difficultés avec Terres d'empreintes qui trouve le devis excessif. Le rendu Madam Artworks est reporté au 30 mars. Un appel client est prévu la semaine prochaine.",
  "raw_transcript": "Alex: Bonjour Marjorie...\nMarjorie: Salut Alex..."
}
```

---

## 9. CLI (Interface Ligne de Commande)

### 9.1 Structure des Commandes

```
voxmind <commande> [options]

Commandes disponibles:
  start           Démarrer une session d'écoute
  stop            Arrêter la session en cours
  status          Afficher le statut actuel
  pause           Mettre en pause la session
  resume          Reprendre après pause
  
  enroll          Enroller une nouvelle voix
  list-speakers   Lister les voix connues
  speaker         Gérer les profils (voir, renommer, supprimer)
  
  transcribe      Transcrire un fichier audio
  session         Gérer les sessions (lister, résumé, détails)
  
  setup           Configuration initiale
  test-audio      Tester les périphériques audio
  download-models Télécharger les modèles ML
  
  version         Afficher la version
  help            Afficher l'aide
```

### 9.2 Mode Interactif

```csharp
// Fichier : VoxMind.CLI/Interactive/InteractiveMode.cs

public class InteractiveMode
{
    public async Task RunAsync(CancellationToken ct)
    {
        var console = new ColorConsole();
        
        console.WriteHeader("VoxMind v1.0.0 - Mode Interactif");
        console.WriteInfo("Tapez 'help' pour la liste des commandes, 'exit' pour quitter\n");
        
        while (!ct.IsCancellationRequested)
        {
            console.WritePrompt();
            var line = Console.ReadLine();
            
            if (string.IsNullOrWhiteSpace(line))
                continue;
                
            var result = await ParseAndExecuteAsync(line, ct);
            
            if (result.ShouldExit)
                break;
                
            console.WriteResult(result);
        }
    }
}

// Exemple d'affichage :
// ┌──────────────────────────────────────┐
// │ 🎤 Session: reunion_marjorie         │
// │ ⏱  Duration: 00:45:23                │
// │ 👥 Participants: Alex, Marjorie     │
// │ 📝 Segments: 127                     │
// └──────────────────────────────────────┘
```

### 9.3 Codes d'Erreur CLI

```csharp
public enum ExitCode
{
    Success = 0,
    GeneralError = 1,
    AudioDeviceError = 2,
    ModelError = 3,
    TranscriptionError = 4,
    DatabaseError = 5,
    SessionError = 6,
    BridgeError = 7,
    InvalidArguments = 8,
    NotListening = 9,
    AlreadyListening = 10,
    PyAnnoteError = 11,
    CanceledByUser = 12
}
```

---

## 10. Tests

### 10.1 Couverture de Tests

```
VoxMind.Tests/
├── Unit/
│   ├── Audio/
│   │   ├── AudioCaptureTests.cs
│   │   ├── AudioConfigurationTests.cs
│   │   ├── AudioChunkTests.cs
│   │   └── AudioDeviceInfoTests.cs
│   ├── Transcription/
│   │   ├── TranscriptionResultTests.cs
│   │   ├── TranscriptionSegmentTests.cs
│   │   ├── WhisperServiceTests.cs
│   │   └── ComputeBackendTests.cs
│   ├── SpeakerRecognition/
│   │   ├── SpeakerProfileTests.cs
│   │   ├── SpeakerEmbeddingTests.cs
│   │   ├── SpeakerIdentificationResultTests.cs
│   │   └── SpeakerMergeTests.cs
│   ├── Session/
│   │   ├── SessionManagerTests.cs
│   │   ├── ListeningSessionTests.cs
│   │   ├── SessionSegmentTests.cs
│   │   └── SummaryGeneratorTests.cs
│   ├── Database/
│   │   ├── VoxMindDbContextTests.cs
│   │   ├── SpeakerProfileRepositoryTests.cs
│   │   └── SessionRepositoryTests.cs
│   ├── Bridge/
│   │   ├── FileBridgeTests.cs
│   │   ├── CommandParsingTests.cs
│   │   └── SystemStatusTests.cs
│   └── Configuration/
│       └── ConfigurationLoaderTests.cs
├── Integration/
│   ├── FullPipelineTests.cs
│   ├── SpeakerEnrollmentFlowTests.cs
│   ├── SessionLifecycleTests.cs
│   ├── BridgeCommunicationTests.cs
│   ├── PyAnnoteIntegrationTests.cs
│   └── WhisperIntegrationTests.cs
├── Performance/
│   ├── LongRunningSessionTests.cs
│   ├── MemoryLeakTests.cs
│   ├── ConcurrentAudioSourceTests.cs
│   └── TranscriptionSpeedTests.cs
└── VoxMind.Tests.csproj
```

### 10.2 Tests Unitaires - Exemples

```csharp
// Fichier : VoxMind.Tests/Unit/Session/SessionManagerTests.cs

public class SessionManagerTests
{
    [Fact]
    public async Task StartSession_WithNoActiveSession_StartsSuccessfully()
    {
        // Arrange
        var mockAudio = new Mock<IAudioCapture>();
        var mockTranscription = new Mock<ITranscriptionService>();
        var mockSpeaker = new Mock<ISpeakerIdentificationService>();
        var mockPyAnnote = new Mock<IPyAnnoteClient>();
        var mockSummary = new Mock<ISummaryGenerator>();
        
        var manager = new SessionManager(
            mockAudio.Object,
            mockTranscription.Object,
            mockSpeaker.Object,
            mockPyAnnote.Object,
            mockSummary.Object,
            NullLogger<SessionManager>.Instance);
        
        // Act
        var session = await manager.StartSessionAsync("test_session");
        
        // Assert
        Assert.NotNull(session);
        Assert.Equal(SessionStatus.Listening, manager.Status);
        Assert.Equal("test_session", session.Name);
        Assert.Null(session.EndedAt);
        Assert.True(manager.IsListening);
    }
    
    [Fact]
    public async Task StopSession_WithoutStart_ThrowsInvalidOperationException()
    {
        // Arrange
        var manager = CreateManager();
        
        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.StopSessionAsync());
    }
    
    [Fact]
    public async Task StopSession_WithActiveSession_StopsAndReturnsSession()
    {
        // Arrange
        var manager = CreateManager();
        var started = await manager.StartSessionAsync();
        
        // Act
        var stopped = await manager.StopSessionAsync();
        
        // Assert
        Assert.Equal(SessionStatus.Idle, manager.Status);
        Assert.False(manager.IsListening);
        Assert.NotNull(stopped.EndedAt);
        Assert.Equal(started.Id, stopped.Id);
    }
    
    [Fact]
    public async Task StartSession_WhenAlreadyListening_ThrowsInvalidOperationException()
    {
        // Arrange
        var manager = CreateManager();
        await manager.StartSessionAsync();
        
        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.StartSessionAsync());
    }
    
    [Fact]
    public async Task PauseAndResume_MaintainsSession()
    {
        // Arrange
        var manager = CreateManager();
        await manager.StartSessionAsync();
        
        // Act
        await manager.PauseSessionAsync();
        Assert.Equal(SessionStatus.Paused, manager.Status);
        
        await manager.ResumeSessionAsync();
        Assert.Equal(SessionStatus.Listening, manager.Status);
    }
}

// Fichier : VoxMind.Tests/Unit/SpeakerRecognition/SpeakerMergeTests.cs

public class SpeakerMergeTests
{
    [Fact]
    public async Task MergeProfiles_TransfersAllEmbeddings()
    {
        // Arrange
        var dbContext = CreateInMemoryDbContext();
        var service = new SpeakerIdentificationService(
            dbContext, mockPyAnnoteClient, Logger);
        
        var profile1 = await service.EnrollSpeakerAsync("Profile1", 
            GenerateEmbedding(), 0.9f);
        var profile2 = await service.EnrollSpeakerAsync("Profile2",
            GenerateEmbedding(), 0.85f);
        
        // Act
        await service.MergeProfilesAsync(profile1.Id, profile2.Id);
        
        // Assert
        var merged = await service.GetProfileAsync(profile1.Id);
        Assert.Equal(2, merged.Embeddings.Count);
        
        var deleted = await service.GetProfileAsync(profile2.Id);
        Assert.Null(deleted);
    }
    
    [Fact]
    public async Task LinkSpeakers_UnknownToKnown_UpdatesUnknownProfile()
    {
        // Arrange
        var service = CreateService();
        var known = await service.EnrollSpeakerAsync("Marjorie",
            GenerateEmbedding(), 0.95f);
        var unknown = await service.EnrollSpeakerAsync("Inconnu #1",
            GenerateEmbedding(), 0.7f);
        
        // Act
        await service.LinkSpeakersAsync(known.Id, unknown.Id);
        
        // Assert
        var updatedUnknown = await service.GetProfileAsync(unknown.Id);
        Assert.Equal("Marjorie", updatedUnknown.Name);
        Assert.Contains("Inconnu #1", updatedUnknown.Aliases);
    }
}
```

### 10.3 Tests d'Intégration

```csharp
// Fichier : VoxMind.Tests/Integration/FullPipelineTests.cs

public class FullPipelineTests : IDisposable
{
    private readonly string _testAudioPath;
    private readonly SessionManager _manager;
    
    public FullPipelineTests()
    {
        _testAudioPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.wav");
        GenerateTestAudio(_testAudioPath, durationSeconds: 10, sampleRate: 16000);
        _manager = CreateRealManager(); // Utilise les vrais services
    }
    
    [Fact]
    public async Task FullPipeline_CaptureToSummary_CompletesSuccessfully()
    {
        // Arrange
        var sessionName = $"test_{Guid.NewGuid()}";
        var segmentCount = 0;
        
        _manager.SegmentProcessed += (s, e) => segmentCount++;
        
        // Act
        await _manager.StartSessionAsync(sessionName);
        
        // Simuler l'arrivée de chunks audio (en pratique,来自 AudioCapture)
        for (int i = 0; i < 5; i++)
        {
            await Task.Delay(500);
            // Trigger segment processed manuellement pour le test
            _manager.OnSegmentProcessed(CreateMockSegment());
        }
        
        var session = await _manager.StopSessionAsync();
        
        // Assert
        Assert.NotNull(session);
        Assert.True(segmentCount > 0);
        Assert.NotNull(session.Summary);
    }
    
    [Fact]
    public async Task LongRunningSession_OneHour_SurvivesWithoutMemoryLeak()
    {
        // Arrange
        await _manager.StartSessionAsync("long_test");
        
        // Act - Simuler 1 heure de session (accéléré)
        for (int i = 0; i < 360; i++) // 360 segments de 10s = 1h
        {
            _manager.OnSegmentProcessed(CreateMockSegment(durationSeconds: 10));
            await Task.Delay(10); // 10ms au lieu de 10s pour le test
        }
        
        var session = await _manager.StopSessionAsync();
        
        // Assert
        Assert.True(session.Duration.TotalMinutes > 50);
        // Vérifier qu'il n'y a pas de mémoire used de manière excessive
    }
    
    public void Dispose()
    {
        _manager?.Dispose();
        if (File.Exists(_testAudioPath))
            File.Delete(_testAudioPath);
    }
}
```

### 10.4 Tests Multi-Backend

```csharp
// Fichier : VoxMind.Tests/Integration/ComputeBackendTests.cs

public class ComputeBackendTests
{
    [Theory]
    [InlineData(ComputeBackend.CPU)]
    public async Task TranscriptionWorks_OnCPU(ComputeBackend backend)
    {
        // Skip si GPU non disponible et qu'on teste pas CPU
        if (backend != ComputeBackend.CPU && !IsBackendAvailable(backend))
            return;
        
        // Arrange
        var service = new WhisperService(backend);
        await service.LoadModelAsync(ModelSize.Tiny, backend);
        
        // Act
        var result = await service.TranscribeFileAsync(_testAudioPath);
        
        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.Text);
        Assert.Equal(backend, service.Info.Backend);
    }
    
    [Fact]
    public void DetectBestBackend_ReturnsAvailableBackend()
    {
        // Act
        var backend = ComputeBackendDetector.DetectBestAvailable();
        
        // Assert
        Assert.True(Enum.IsDefined(typeof(ComputeBackend), backend));
        Assert.True(backend != ComputeBackend.Auto);
    }
}
```

### 10.5 Tests de Robustesse

```csharp
// Fichier : VoxMind.Tests/Performance/RobustnessTests.cs

public class RobustnessTests
{
    [Fact]
    public async Task HandlesPoorAudioQuality_WithoutCrashing()
    {
        // Générer audio avec beaucoup de bruit
        var noisyAudio = GenerateNoisyAudio(durationSeconds: 5);
        
        // Act & Assert - Ne doit pas crash
        var result = await _transcription.TranscribeChunkAsync(noisyAudio);
        Assert.NotNull(result);
    }
    
    [Fact]
    public async Task HandlesMultipleSimultaneousSpeakers()
    {
        // Audio avec 3 locuteurs qui parlent en même temps
        var overlappingAudio = GenerateOverlappingSpeech(
            speakers: 3,
            durationSeconds: 10
        );
        
        var result = await _transcription.TranscribeChunkAsync(overlappingAudio);
        
        // Le texte devrait contenir des contributions de plusieurs speakers
        Assert.NotEmpty(result.Text);
    }
    
    [Fact]
    public async Task HandlesStrongAccent()
    {
        var accentedAudio = GenerateAccentedAudio(
            accent: "SouthernFrench",
            durationSeconds: 5
        );
        
        var result = await _transcription.TranscribeChunkAsync(accentedAudio);
        
        // Le modèle devrait quand même transcrire (avec peut-être moins de confiance)
        Assert.NotEmpty(result.Text);
    }
    
    [Fact]
    public async Task HandlesBackgroundNoise()
    {
        var audioWithBackground = GenerateAudioWithBackground(
            speechDuration: 10,
            backgroundSound: "咖啡_shop"
        );
        
        var result = await _transcription.TranscribeChunkAsync(audioWithBackground);
        
        Assert.NotNull(result);
        Assert.True(result.Confidence > 0.3); // Devrait être détectable malgré le bruit
    }
}
```

### 10.6 Validation et Couverture

```bash
# Lancer tous les tests
dotnet test VoxMind.sln

# Lancer avec couverture
dotnet test VoxMind.sln --collect:"XPlat Code Coverage"
dotnet reportgenerator -reports:coverage.cobertura.xml -targetdir:coverage_report

# Lancer tests spécifiques
dotnet test --filter "Category=Unit"
dotnet test --filter "Category=Integration"
dotnet test --filter "FullyQualifiedName~SessionManagerTests"

# Build de release + tests
dotnet build VoxMind.sln -c Release
dotnet test VoxMind.sln -c Release --no-build

# Benchmarks
dotnet run --project VoxMind.Tests/Benchmarks/
```

---

## 11. Modèles et Dépendances

### 11.0 Recherche Context7 pour Exemples de Code

**Context7** (context7.com) est un outil permettant d'interroger la documentation de bibliothèques en temps réel via une API. Il doit être utilisé pour générer du code de qualité avec les bonnes pratiques.

#### Configuration Context7

```bash
# Installation du client Context7
npm install -g @context7/context7

# Configuration de la clé API (obtenir sur context7.com)
export CONTEXT7_API_KEY=votre_cle_api
```

#### Utilisation dans le Projet

```bash
# Exemple: Obtenir la dernière documentation PyAnnote
context7 query "pyannote.audio speaker embedding extraction python example 2024"

# Exemple: Obtenir la documentation TorchSharp
context7 query "TorchSharp tensor creation gpu cuda example"

# Exemple: Obtenir la documentation Whisper
context7 query "openai whisper transcription python api example"
```

#### Intégration dans le Workflow Claude Code

Le développeur doit utiliser Context7 pour :

1. **PyAnnote** — Vérifier les dernières API d'extraction d'embeddings
   ```bash
   context7 query "pyannote.audio 3.1 embedding extraction pipeline python 2024"
   ```

2. **TorchSharp** — Obtenir les exemples de code PyTorch pour C#
   ```bash
   context7 query "TorchSharp neural network module example cuda 2024"
   ```

3. **Whisper.cpp / Whisper** — Documentation pour l'intégration
   ```bash
   context7 query "whisper transcription python example realtime"
   ```

4. **gRPC C#** — Meilleures pratiques pour l'implémentation
   ```bash
   context7 query "grpc csharp server client async example 2024"
   ```

5. **EF Core SQLite** — Patterns de migration et requêtage
   ```bash
   context7 query "Entity Framework Core sqlite async operations 2024"
   ```

#### Script d'Exemples Automatisés

```bash
#!/bin/bash
# generate_examples.sh - Génère des exemples de code actualisés

MODELS=(
    "pyannote.audio speaker embedding python"
    "TorchSharp convolution neural network example"
    "whisper cpp python binding example"
    "grpc python async streaming example"
    "NAudio dotnet audio capture example"
)

for query in "${MODELS[@]}"; do
    echo "=== $query ==="
    context7 query "$query"
    echo ""
done > docs/code_examples_context7.md
```

#### Points d'Attention

- **Toujours vérifier la date** des exemples (préférer 2023-2024)
- **Valider la compatibilité** des APIs avec les versions choisies
- **Adapter les exemples** au contexte C#/Python du projet
- **Ne pas copier bêtement** — comprendre et intégrer proprement

#### Ressources Context7

- **Site web** : https://context7.com
- **Documentation** : https://docs.context7.com
- **Bibliothèques supportées** : PyTorch, TensorFlow, OpenAI, HuggingFace, PyAnnote, et +200 autres

### 11.1 Packages NuGet

### 11.1 Packages NuGet

```xml
<!-- VoxMind.Core/VoxMind.Core.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="8.0.0" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="8.0.0">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers</IncludeAssets>
    </PackageReference>
    <PackageReference Include="TorchSharp" Version="0.102.1" />
    <PackageReference Include="Grpc.Net.Client" Version="2.60.0" />
    <PackageReference Include="Grpc.Tools" Version="2.60.0">
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="8.0.0" />
    <PackageReference Include="Serilog" Version="3.1.1" />
    <PackageReference Include="Serilog.Extensions.Hosting" Version="8.0.0" />
    <PackageReference Include="Serilog.Sinks.Console" Version="5.0.1" />
    <PackageReference Include="Serilog.Sinks.File" Version="5.0.0" />
    <PackageReference Include="System.Text.Json" Version="8.0.0" />
    <PackageReference Include="YamlDotNet" Version="15.1.0" />
  </ItemGroup>

  <ItemGroup>
    <Protobuf Include="../python_services/protos/speaker.proto" GrpcServices="Client" />
  </ItemGroup>
</Project>
```

### 11.2 Services Python (requirements.txt)

```text
# python_services/requirements.txt

# Core
torch>=2.2.0
torchvision>=0.17.0  # Pour certains modèles

# PyAnnote pour identification des locuteurs
pyannote.audio>=3.1.0
pyannote.database>=5.1.0
pyannote.metrics>=3.2.0
pyannote.pipeline>=3.0.5

# Audio processing
numpy>=1.26.0
scipy>=1.12.0
librosa>=0.10.0
soundfile>=0.13.0
pedalboard>=0.5.0  # Audio effects

# gRPC
grpcio>=1.60.0
grpcio-tools>=1.60.0
protobuf>=4.25.0

# Utils
tqdm>=4.66.0
pandas>=2.1.0
```

### 11.3 Modèles à Télécharger

| Modèle | Taille | Commande/Téléchargement |
|--------|--------|------------------------|
| Whisper Tiny | ~75 MB | `python -m whisper.download --model tiny` |
| Whisper Base | ~140 MB | `python -m whisper.download --model base` |
| Whisper Small | ~465 MB | `python -m whisper.download --model small` |
| Whisper Medium | ~1.5 GB | `python -m whisper.download --model medium` |
| PyAnnote Segmentation | ~200 MB | `pyannote.audio download-segmentation` |
| PyAnnote Embedding | ~80 MB | `pyannote.audio download-speaker-embedding` |

```bash
# Script de téléchargement
python python_services/download_models.py \
    --whisper base \
    --pyannote \
    --output /home/pc/voice_data/cache
```

---

## 12. Installation et Configuration

### 12.1 Prérequis Système

```bash
# Ubuntu/Debian 22.04+
sudo apt-get update
sudo apt-get install -y \
    python3.10 \
    python3.10-venv \
    python3-pip \
    portaudio19-dev \
    libportaudio2 \
    libasound2-dev \
    ffmpeg

# Pour CUDA (NVIDIA)
# Installer CUDA Toolkit 12.x depuis nvidia.com

# Pour ROCm (AMD GPU Linux)
# Installer ROCm depuis amd.com
```

### 12.2 Installation Automatisée

```bash
#!/bin/bash
# install.sh - Script d'installation complet

set -e

echo "🚀 Installation de VoxMind..."

# 1. Prérequis
echo "📦 Installation des prérequis système..."
if command -v apt-get &> /dev/null; then
    sudo apt-get update
    sudo apt-get install -y python3.10 python3-pip portaudio19-dev libportaudio2 ffmpeg
elif command -v pacman &> /dev/null; then
    sudo pacman -S python python-pip portaudio ffmpeg
fi

# 2. Python venv
echo "🐍 Création de l'environnement Python..."
python3 -m venv venv
source venv/bin/activate
pip install --upgrade pip
pip install -r python_services/requirements.txt

# 3. .NET
echo "📦 Installation de .NET 8..."
if ! command -v dotnet &> /dev/null; then
    wget https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb
    sudo dpkg -i packages-microsoft-prod.deb
    sudo apt-get update
    sudo apt-get install -y dotnet-sdk-8.0
fi

# 4. Télécharger les modèles
echo "🤖 Téléchargement des modèles ML..."
python python_services/download_models.py --whisper base --pyannote

# 5. Build
echo "🔨 Build du projet..."
dotnet build VoxMind.sln

# 6. Configuration
echo "⚙️ Configuration..."
cp voice_data/config/config.example.json voice_data/config/config.json

echo "✅ Installation terminée !"
echo "Tapez './voxmind setup' pour configurer."
```

### 12.3 Configuration

```json
// voice_data/config/config.json

{
  "$schema": "./config.schema.json",
  "application": {
    "name": "VoxMind",
    "version": "1.0.0",
    "environment": "development"
  },
  "audio": {
    "default_sample_rate": 16000,
    "default_chunk_duration_ms": 100,
    "sources": {
      "micro": {
        "enabled": true,
        "device_index": -1,
        "name": "default"
      },
      "system": {
        "enabled": false,
        "device_index": -1,
        "name": "default"
      }
    },
    "max_silent_duration_ms": 30000
  },
  "ml": {
    "transcription": {
      "model": "base",
      "model_path": "/home/pc/voice_data/cache/whisper",
      "language": "auto",
      "compute_backend": "auto",
      "temperature": 0.0
    },
    "speaker_recognition": {
      "enabled": true,
      "pyannote_endpoint": "http://localhost:50051",
      "confidence_threshold": 0.7,
      "embedding_size": 512,
      "max_enrollment_duration_seconds": 60
    }
  },
  "database": {
    "path": "/home/pc/voice_data/profiles/database.sqlite",
    "backup_enabled": true,
    "backup_interval_hours": 24,
    "backup_path": "/home/pc/voice_data/profiles/backups"
  },
  "session": {
    "output_folder": "/home/pc/voice_data/sessions",
    "summary_interval_minutes": 5,
    "max_segment_duration_seconds": 30,
    "save_audio_cache": false,
    "audio_cache_format": "wav"
  },
  "bridge": {
    "shared_folder": "/home/pc/voice_data/shared",
    "poll_interval_ms": 500,
    "command_timeout_seconds": 30,
    "status_update_interval_seconds": 5
  },
  "logging": {
    "level": "Information",
    "console": {
      "enabled": true,
      "format": "colored"
    },
    "file": {
      "enabled": true,
      "path": "/home/pc/voice_data/logs/voxmind_{date}.log",
      "rolling_interval": "Day",
      "retained_file_count": 30
    }
  },
  "metrics": {
    "enabled": false,
    "port": 9090,
    "endpoint": "/metrics"
  }
}
```

### 12.4 Lancement

```bash
# Build
dotnet build VoxMind.sln -c Release

# Lancer les tests
dotnet test VoxMind.sln -c Release

# Lancer l'application
dotnet run --project src/VoxMind.CLI/VoxMind.CLI.csproj

# Mode interactif
./voxmind

# Commandes directes
./voxmind start --name "reunion_marjorie"
./voxmind status
./voxmind stop

# Avec configuration personnalisée
./voxmind --config /chemin/vers/config.json

# Mode service (arrière-plan)
./voxmind start --daemon
```

---

## 13. CI/CD

### 13.1 GitHub Actions - Build

```yaml
# .github/workflows/build.yml

name: Build and Test

on:
  push:
    branches: [ main, develop ]
  pull_request:
    branches: [ main ]

jobs:
  build:
    runs-on: ${{ matrix.os }}
    strategy:
      matrix:
        os: [ubuntu-22.04, windows-latest]
        dotnet: ['8.0']
        
    steps:
    - uses: actions/checkout@v4
    
    - name: Setup .NET
      uses: actions/setup-dotnet@v4
      with:
        dotnet-version: ${{ matrix.dotnet }}
    
    - name: Restore dependencies
      run: dotnet restore VoxMind.sln
    
    - name: Build
      run: dotnet build VoxMind.sln -c Release --no-restore
    
    - name: Test
      run: dotnet test VoxMind.sln -c Release --no-build --verbosity normal
    
    - name: Upload coverage
      uses: codecov/codecov-action@v3
      with:
        files: ./coverage/cobertura.xml

  style-check:
    runs-on: ubuntu-22.04
    steps:
    - uses: actions/checkout@v4
    - name: Check format
      run: |
        dotnet tool install --global dotnet-format
        dotnet format --verify-no-changes VoxMind.sln
```

### 13.2 GitHub Actions - Release

```yaml
# .github/workflows/release.yml

name: Release

on:
  push:
    tags:
      - 'v*'

jobs:
  release:
    runs-on: ubuntu-22.04
    permissions:
      contents: write
    
    steps:
    - uses: actions/checkout@v4
    
    - name: Setup .NET
      uses: actions/setup-dotnet@v4
      with:
        dotnet-version: '8.0'
    
    - name: Build Release
      run: |
        dotnet build VoxMind.sln -c Release
        dotnet publish src/VoxMind.CLI -c Release -o ./publish/cli
        dotnet publish src/VoxMind.Core -c Release -o ./publish/core
        
    - name: Create ZIP
      run: |
        zip -r VoxMind-${{ github.ref_name }}-linux-x64.zip ./publish
        zip -r VoxMind-${{ github.ref_name }}-windows-x64.zip ./publish
        
    - name: Create Python Service Package
      run: |
        cd python_services
        zip -r ../VoxMind-PythonServices-${{ github.ref_name }}.zip .
        
    - name: Create Release
      uses: softprops/action-gh-release@v1
      with:
        files: |
          VoxMind-*.zip
        draft: true
      env:
        GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}
```

---

## 14. Conteneurisation (Docker)

### 14.1 Docker Compose

```yaml
# docker-compose.yml

version: '3.8'

services:
  voxmind:
    build:
      context: .
      dockerfile: Dockerfile
    image: voxmind:latest
    container_name: voxmind
    environment:
      - PYTANNOTE_ENDPOINT=http://pyannote:50051
      - DATABASE_PATH=/data/profiles/database.sqlite
      - SHARED_FOLDER=/data/shared
      - LOG_LEVEL=Information
    volumes:
      - ./voice_data:/data
      - /dev/snd:/dev/snd  # Audio devices (Linux only)
    devices:
      - /dev/snd:/dev/snd
    restart: unless-stopped
    depends_on:
      - pyannote
    networks:
      - voxmind-network

  pyannote:
    build:
      context: ./python_services
      dockerfile: Dockerfile.pyannote
    image: voxmind-pyannote:latest
    container_name: voxmind-pyannote
    environment:
      - GRPC_PORT=50051
      - CUDA_VISIBLE_DEVICES=0  # Pour GPU NVIDIA
    deploy:
      resources:
        reservations:
          devices:
            - driver: nvidia
              count: 1
              capabilities: [gpu]
    restart: unless-stopped
    networks:
      - voxmind-network

networks:
  voxmind-network:
    driver: bridge
```

### 14.2 Dockerfile Python (PyAnnote)

```dockerfile
# python_services/Dockerfile.pyannote

FROM nvidia/cuda:12.2.0-runtime-ubuntu22.04

ENV DEBIAN_FRONTEND=noninteractive
ENV PYTHONUNBUFFERED=1

# Prérequis
RUN apt-get update && apt-get install -y \
    python3.10 \
    python3-pip \
    portaudio19-dev \
    libportaudio2 \
    ffmpeg \
    && rm -rf /var/lib/apt/lists/*

# Copier les fichiers Python
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# Télécharger les modèles
COPY download_models.py .
RUN python download_models.py --pyannote --output /models

# Code du serveur
COPY pyannote_server.py .
COPY protos/ ./protos/

# Compiler les protos
RUN python -m grpc_tools.protoc \
    -I./protos \
    --python_out=. \
    --grpc_python_out=. \
    protos/speaker.proto

EXPOSE 50051

CMD ["python", "pyannote_server.py", "--models-path", "/models"]
```

---

## 15. Guide Utilisateur

### 15.1 Installation Rapide

```bash
# 1. Cloner ou télécharger le projet
cd VoxMind

# 2. Lancer le script d'installation
chmod +x install.sh
./install.sh

# 3. Premier lancement
./voxmind setup
# Suivre les instructions pour configurer les périphériques audio

# 4. Tester
./voxmind test-audio
```

### 15.2 Commandes CLI

```bash
# Mode interactif
$ ./voxmind
VoxMind> help
VoxMind> status
VoxMind> list-speakers

# Session d'écoute
$ ./voxmind start                    # Démarrer avec nom auto
$ ./voxmind start --name "reunion"   # Démarrer avec nom
$ ./voxmind pause                    # Pause
$ ./voxmind resume                  # Reprendre
$ ./voxmind stop                    # Arrêter (OBLIGATOIRE)

# Transcription
$ ./voxmind transcribe audio.wav     # Transcrire un fichier
$ ./voxmind transcribe --file audio.mp3 --output result.txt

# Gestion des voix
$ ./voxmind enroll "Marjorie"        # Enroller une nouvelle voix
$ ./voxmind list-speakers           # Lister les voix connues
$ ./voxmind speaker Marjorie         # Détails sur Marjorie
$ ./voxmind speaker Marjorie --rename "Marjorie D."
$ ./voxmind speaker Marjorie --delete

# Sessions
$ ./voxmind sessions               # Lister les sessions
$ ./voxmind session abc123 --summary # Résumé de la session
$ ./voxmind session abc123 --details # Tous les détails

# Configuration
$ ./voxmind setup                   # Configuration initiale
$ ./voxmind config --show           # Afficher la config
$ ./voxmind config --set ml.transcription.model=small
```

### 15.3 Intégration avec Cortana/OpenClaw

```bash
# Activer l'écoute via fichier (depuis n'importe quel canal)
echo '{
  "command": "START_LISTENING",
  "parameters": { "session_name": "appel_marjorie" },
  "timestamp": "2026-03-22T14:30:00Z"
}' > /home/pc/voice_data/shared/commands_to_voxmind.json

# Vérifier le statut
cat /home/pc/voice_data/shared/status_from_voxmind.json

# Arrêter
echo '{"command": "STOP_LISTENING"}' > /home/pc/voice_data/shared/commands_to_voxmind.json

# Obtenir le résumé
echo '{"command": "GET_LAST_SUMMARY"}' > /home/pc/voice_data/shared/commands_to_voxmind.json
cat /home/pc/voice_data/shared/status_from_voxmind.json
```

---

## 16. Considérations Techniques

### 16.1 Performance

| Métrique | Cible | Notes |
|----------|-------|-------|
| Latence transcription | < 500ms | Du mot prononcé à l'affichage |
| Latence identification | < 100ms | Embedding extraction |
| Mémoire (idle) | < 500 MB | Sans modèle chargé |
| Mémoire (actif) | < 4 GB | Avec Whisper Base |
| CPU (actif) | 5-15% | Sur CPU moderne |
| GPU (actif) | < 20% | NVIDIA RTX 3080 |

### 16.2 Limites Connues

- **PyAnnote** nécessite Python 3.10+ et dépendances audio natives
- **GPU recommandé** pour transcription temps réel (CPU possible mais lent)
- **音频 avec acc码 fort** peut réduire la précision
- **Locuteurs similaires** (jumeaux, voix très proches) peuvent être mal identifiés

### 16.3 Sécurité

- Toutes les données **restent locales** (pas de cloud)
- Base SQLite **non chiffrée** par défaut
- Logs ne contiennent pas d'audio, uniquement transcriptions
- Pas de transmission réseau des données audio

### 16.4 Extensibilité

- Interface `IAudioCapture` pour supporter de nouvelles sources
- Interface `ITranscriptionService` pour changer de moteur (DeepSpeech, etc.)
- Interface `ISpeakerIdentificationService` pour alternative à PyAnnote
- Support gRPC pour communication inter-processus ou réseau

---

## 17. Évolutions Futures Possibles

- [ ] **Interface web** de visualisation des sessions
- [ ] **Intégration calendrier** (détection automatique de réunions)
- [ ] **Transcription multilingue** simultanée
- [ ] **Synthèse vocale** des résumés (TTS)
- [ ] **Détection émotions/sentiments**
- [ ] **Export** vers Notion, Obsidian, outils tiers
- [ ] **API REST** pour contrôle externe
- [ ] **Application mobile** pour suivi à distance
- [ ] **Webhooks** pour notifications
- [ ] **Chiffrement** de la base de données
- [ ] **Authentification** pour l'accès aux sessions
- [ ] **Compression** des fichiers audio cached
- [ ] **Indexation** des sessions pour recherche full-text

---

## LICENSE

MIT License - Voir fichier LICENSE

---

## CHANGELOG

### v1.0.0 (2026-03-22)
- Version initiale
- Transcription Whisper (CPU/CUDA/ROCm)
- Identification locuteurs PyAnnote
- Mode écoute continu
- Interface Bridge file-based
- CLI complet
- Tests unitaires et d'intégration
