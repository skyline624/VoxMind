# VoxMind — image Docker tout-en-un (.NET + Qwen3-TTS vLLM)

Une **seule image** embarquant tous les services :
- **VoxMind.Api** (.NET) sur `:8000` — transcription (Parakeet), reconnaissance de locuteur, TTS Kokoro (ONNX CPU, in-process).
- **Sidecar TTS** servi par **vLLM-omni** (GPU) sur `127.0.0.1:8091` — tier expressif **temps réel** (RTF ~0,5 sur RTX 3090), appelé en HTTP par VoxMind (moteur `qwen3`). Modèle **basculable** entre **Qwen3-TTS** (clonage) et **Voxtral-TTS** (Mistral, presets) via `TTS_BACKEND` — voir la section dédiée.

Les deux process sont gérés par `supervisord` dans le conteneur.

## Build (depuis la racine du repo)

```bash
docker build -f docker/voxmind/Dockerfile -t voxmind:aio .
```

## Run

```bash
docker run --gpus all -p 8000:8000 \
  -v "$PWD/models:/app/models" \
  -v "$PWD/voice_data:/app/voice_data" \
  -v voxmind-hf:/root/.cache/huggingface \
  voxmind:aio
```

- `models/` (hôte) → modèles ONNX VoxMind (Kokoro, Parakeet, espeak-ng-data…). **À monter** (non bakés).
- `voice_data/` → base SQLite, config, sessions, logs.
- `voxmind-hf` (volume nommé) → poids du modèle Qwen3-TTS, **téléchargés au 1er démarrage** (~3,5 Go).

> **1er démarrage** : le sidecar vLLM charge le 1.7B + capture les CUDA graphs (~3-5 min). Pendant cette
> fenêtre, `model: "qwen3"` répond **503** ; **Kokoro reste disponible** immédiatement. Une fois prêt, les logs
> affichent le serveur vLLM en écoute sur `:8091`.

## Utilisation

Santé : `GET http://localhost:8000/health`

Synthèse expressive (Qwen3-TTS via vLLM, temps réel, streaming) :
```bash
curl -N -X POST http://localhost:8000/v1/audio/speech \
  -H 'Content-Type: application/json' \
  -d '{ "input": "Bonjour, ravi de vous lire.", "language": "fr", "model": "qwen3",
        "instructions": "d'\''un ton enjoué", "response_format": "pcm" }' --output out.pcm
```
- `model` : `qwen3` (vLLM, expressif live) ou `kokoro` (CPU, ultra-rapide, neutre). Absent → défaut config (`qwen3`).
- `instructions` : contrôle d'émotion/style en langage naturel (propre à `qwen3`).
- `response_format` : `pcm` (PCM16 brut 24 kHz, recommandé pour le streaming) ou `wav`.

## Configuration

La config par défaut (`config.default.json`) fixe `ml.tts.default_engine = "qwen3"` et
`ml.tts.qwen3_vllm.base_url = http://127.0.0.1:8091`. Pour la personnaliser, monter un
`voice_data/config/config.json` (snake_case ; cf. `AppConfiguration`).

Variables d'env utiles : `QWEN3_MODEL` (modèle servi, défaut 1.7B-CustomVoice), `VLLM_PORT` (8091),
`VOXMIND_MODELS_DIR`, `VOXMIND_DATA_DIR`, et surtout les deux leviers **VRAM** ci-dessous.

### Empreinte GPU (important sur carte partagée)

Le sidecar vLLM réserve **~19,5 Go** sur une 3090 quand il tourne. **Cette empreinte est inhérente au
pipeline temps réel** et mesurée constante : capture des **CUDA graphs** (indispensables au RTF ~0,5) +
pools KV du stage *code2wav* (`max_model_len=65536`) + contexte torch/CUDA. Constat empirique :

- ❌ baisser `gpu_memory_utilization` (CLI ou par stage via `QWEN3_STAGE_GPU_UTIL`) **ne la réduit pas** —
  ça ne plafonne que le cache KV, marginal ici.
- ❌ servir le **0.6B** au lieu du 1.7B **ne la réduit pas non plus** (poids ≠ poste dominant).
- ✅ désactiver les CUDA graphs libérerait de la VRAM mais **tuerait le RTF** (retour à ~2-5) → exclu.

**Donc :** réserver ~20 Go est le coût d'un Qwen3-TTS **expressif en temps réel** sur cette carte. Sur une 3090
partagée :
- garder le sidecar actif uniquement quand on veut le TTS expressif ; sinon **arrêter le conteneur** ou poser
  `ml.tts.qwen3_vllm.enabled = false` → **Kokoro** (CPU, 0 Go GPU, RTF 0,05) couvre le TTS sans toucher au GPU ;
- `QWEN3_STAGE_GPU_UTIL` (défaut `0.2`) reste réglable mais n'a qu'un effet marginal.

## Clonage de voix (Qwen3-TTS Base)

Qwen3-TTS est une **famille** : `…-CustomVoice` (9 voix prédéfinies, défaut) et **`…-Base`** (clonage depuis
un audio de référence). **Un seul modèle par conteneur** — pour cloner, on sert le modèle Base.

1. **Préparer la référence** (~3-10 s, mono 24 kHz) et la placer dans `voice_data/voices/` :
   ```bash
   ffmpeg -i source.wav -ar 24000 -ac 1 voice_data/voices/ref_clip.wav
   ```
2. **Servir le modèle Base** : `-e QWEN3_MODEL=Qwen/Qwen3-TTS-12Hz-1.7B-Base`.
3. **Config** `voice_data/config/config.json` → `ml.tts.qwen3_vllm` :
   ```json
   {
     "task_type": "Base",
     "model": "Qwen/Qwen3-TTS-12Hz-1.7B-Base",
     "reference_audio_path": "/app/voice_data/voices/ref_clip.wav",
     "reference_text": "Transcription exacte de l'audio de référence.",
     "reference_voice_name": "voxmind_clone"
   }
   ```
   - **`reference_text`** présent → mode **ICL** (meilleure fidélité). Absent → clonage par **embedding** seul.
   - `reference_text` peut être obtenu via le STT intégré : `POST /v1/audio/transcriptions` sur la référence.

Ensuite, l'**API normale** rend la voix clonée — rien de spécial côté client :
```bash
curl -X POST http://localhost:8090/v1/audio/speech -H 'Content-Type: application/json' \
  -d '{"model":"qwen3","input":"N'\''importe quel texte.","response_format":"wav"}' -o out.wav
```

**Fonctionnement** : à la 1ʳᵉ requête, le service **enregistre la voix** auprès du sidecar
(`POST /v1/audio/voices` — qui calcule le `ref_code` requis par l'ICL et stocke l'embedding + le `ref_text`),
puis chaque synthèse **cible la voix par nom**. L'enregistrement est idempotent et **réémis automatiquement**
si le sidecar redémarre (les voix sont gardées en mémoire). RTF mesuré **~0,57** (temps réel) sur RTX 3090.

> ⚠️ On n'envoie **pas** `ref_audio` inline en ICL : le moteur vLLM-omni crashe alors (« ref_audio artifact
> cache entry is missing ref_code »). Le passage par l'upload de voix est ce qui calcule ce `ref_code`.
> Si `task_type=Base` sans `reference_audio_path` valide, `qwen3` se déclare **non chargé** (→ 503), pas 400.

## Basculer entre Qwen3-TTS et Voxtral-TTS

Le sidecar peut servir **Qwen3-TTS** (Alibaba, clonage de voix) ou **Voxtral-TTS** (Mistral, 20 voix presets)
— tous deux via vLLM-omni, même endpoint `/v1/audio/speech`. **Un seul modèle à la fois** (chaque pipeline
réserve ~20-22 Go sur une 24 Go). Le choix se fait par la variable **`TTS_BACKEND`** (`qwen3` | `voxtral`) ;
l'entrypoint aligne automatiquement la config VoxMind (`model`, `voice`, `task_type`) sur le backend.

| Backend | Modèle | Voix | Clonage |
|---|---|---|---|
| `qwen3` | `Qwen/Qwen3-TTS-12Hz-1.7B-Base` | ta voix clonée (`voxmind_clone_xv`) ou 9 presets (`CustomVoice`) | ✅ |
| `voxtral` | `mistralai/Voxtral-4B-TTS-2603` | 20 presets (`fr_female`, `fr_male`, `neutral_female`…) | ❌ (poids d'encodeur non publiés) |

**Script de bascule** (Windows/PowerShell, recrée le conteneur) :
```powershell
.\docker\voxmind\switch-tts.ps1 voxtral                    # → Voxtral, fr_female
.\docker\voxmind\switch-tts.ps1 voxtral -VoxtralVoice fr_male
.\docker\voxmind\switch-tts.ps1 qwen3                       # → Qwen3, ta voix clonée
```
Ou en `docker run` direct : `-e TTS_BACKEND=voxtral` (+ `-e VOXTRAL_VOICE=fr_male` au besoin).

- Licence : Qwen3-TTS = **Apache 2.0** (commercial OK) ; Voxtral-TTS = **CC BY-NC 4.0** (non-commercial).
- Côté client (anythingLLM, SDK OpenAI…) : rien ne change, on garde `model:"qwen3"` — c'est le backend servi qui
  change. Voix Voxtral : les 20 presets sont `ar_male, casual_female, casual_male, cheerful_female, de_female,
  de_male, es_female, es_male, fr_female, fr_male, hi_female, hi_male, it_female, it_male, neutral_female,
  neutral_male, nl_female, nl_male, pt_female, pt_male`.
- Au switch, le sidecar recharge le nouveau modèle (~3-5 min ; 1ᵉʳ passage sur Voxtral = download ~8 Go).

## Notes

- Le GPU n'est utilisé que par vLLM (~0,85 de 24 Go). Les engines ONNX .NET tournent sur **CPU** → pas de
  contention, et on évite le conflit `onnxruntime-gpu`.
- Pour un parc **sans GPU**, mettre `ml.tts.qwen3_vllm.enabled = false` et `default_engine = "kokoro"`
  (le conteneur vLLM ne démarrera pas utilement — préférer alors un déploiement Kokoro seul).
