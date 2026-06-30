# Phase 0 — Build CUDA de qwen3-tts.cpp + bench RTF (gate GO/NO-GO)

Objectif : faire tourner **Qwen3-TTS** (port C++ `qwen3-tts.cpp`, GGML, **zéro Python / zéro ONNX au runtime**)
sur la **RTX 3090** et mesurer le **RTF**. Gate de décision : **RTF médian ≤ 1.0** sur texte FR long → on
poursuit l'intégration `.NET` (P/Invoke de `qwen3tts.dll`). Sinon on réévalue (repli Orpheus).

> ⚠️ Cette recette n'a **pas pu être compilée ici** (pas de 3090 / toolchain dans l'environnement d'analyse).
> Les 3 points de friction probables sont signalés `⚠️ À VALIDER`. Le code C++ est backend-agnostique ;
> le seul vrai obstacle est le `CMakeLists.txt` qui ne linke pas le backend CUDA.

---

## 0. Prérequis (Windows, machine de la 3090)

- **CUDA Toolkit 12.x** (https://developer.nvidia.com/cuda-downloads) — fournit `nvcc`, `cudart`, `cublas`.
- **Visual Studio 2022 Build Tools** (workload « Desktop development with C++ », MSVC + Windows SDK).
- **CMake ≥ 3.18** (le projet exige 3.14, CUDA confortable à partir de 3.18).
- **Python 3.10+ + `uv`** (uniquement pour la conversion *offline* des poids — pas au runtime).
- Driver NVIDIA récent (≥ 550) compatible CUDA 12.

Vérifs rapides (PowerShell) :
```powershell
nvcc --version
nvidia-smi
cmake --version
```

---

## 1. Cloner le projet + sous-module ggml

```powershell
git clone --recursive https://github.com/predict-woo/qwen3-tts.cpp.git
cd qwen3-tts.cpp
# si oubli du --recursive :
git submodule update --init --recursive
```

---

## 2. Télécharger + convertir les modèles en GGUF (offline, Python OK)

```powershell
uv venv
.\.venv\Scripts\Activate.ps1
uv pip install huggingface_hub gguf torch safetensors numpy tqdm

# One-shot : download HF + conversion GGUF (0.6B + tokenizer)
python scripts/setup_pipeline_models.py --models-dir .\models
```

Résultat attendu dans `.\models\` :
- `qwen3-tts-0.6b-f16.gguf`
- `qwen3-tts-tokenizer-f16.gguf`

> Équivalent manuel si besoin :
> ```powershell
> python scripts/convert_tts_to_gguf.py       --input .\models\Qwen3-TTS-12Hz-0.6B-Base --output .\models\qwen3-tts-0.6b-f16.gguf
> python scripts/convert_tokenizer_to_gguf.py --input .\models\Qwen3-TTS-Tokenizer-12Hz  --output .\models\qwen3-tts-tokenizer-f16.gguf
> ```
> Le modèle **1.7B** (qualité supérieure) se convertit pareil depuis `Qwen/Qwen3-TTS-12Hz-1.7B-Base`
> — à tester en second si le 0.6B passe le gate avec de la marge.

---

## 3. Compiler GGML avec CUDA (en SHARED → DLL autoportantes)

On construit ggml **avant** le projet, comme le top-level CMake l'attend (`./ggml/build/src`).
`BUILD_SHARED_LIBS=ON` fait que `ggml-cuda.dll` embarque ses dépendances CUDA (cudart/cublas) → linkage trivial.

```powershell
cmake -S ggml -B ggml/build `
  -DGGML_CUDA=ON `
  -DGGML_METAL=OFF `
  -DBUILD_SHARED_LIBS=ON `
  -DCMAKE_CUDA_ARCHITECTURES=86 `
  -DCMAKE_BUILD_TYPE=Release
cmake --build ggml/build --config Release -j
```

> `⚠️ À VALIDER` — l'emplacement exact des libs après build. Repère où sont posés
> `ggml.dll`, `ggml-base.dll`, `ggml-cpu.dll`, `ggml-cuda.dll` (et `.lib`) :
> ```powershell
> Get-ChildItem -Recurse ggml/build -Include *.dll,*.lib | Select-Object FullName
> ```
> Note le dossier de `ggml-cuda.lib` — tu en auras besoin au §4 (souvent `ggml/build/src/`
> ou `ggml/build/src/ggml-cuda/`, selon la version du sous-module).

---

## 4. Patcher `CMakeLists.txt` pour linker le backend CUDA

Le projet ne linke `ggml-cuda` nulle part. Deux approches — **A recommandée**.

### Option A (recommandée) — déléguer à ggml via `add_subdirectory`

Le plus robuste : pas de chemin en dur, dépendances CUDA transitives gérées par CMake.
Remplace le bloc de détection GGML (autour des lignes 32-43) par :

```cmake
set(GGML_DIR "${CMAKE_CURRENT_SOURCE_DIR}/ggml")

option(QWEN3_TTS_CUDA "Build & link the GGML CUDA backend (NVIDIA GPU)" OFF)

if(QWEN3_TTS_CUDA)
    set(GGML_CUDA       ON  CACHE BOOL "" FORCE)
    set(GGML_METAL      OFF CACHE BOOL "" FORCE)
    set(BUILD_SHARED_LIBS ON CACHE BOOL "" FORCE)
    add_subdirectory(${GGML_DIR} ggml-build)   # définit les cibles ggml / ggml-base / ggml-cpu / ggml-cuda
    set(GGML_HAS_METAL OFF)
    set(GGML_HAS_CUDA  ON)
    set(GGML_BUILD_DIR "${CMAKE_BINARY_DIR}/ggml-build")
else()
    set(GGML_BUILD_DIR "${GGML_DIR}/build")
    if(APPLE AND EXISTS "${GGML_BUILD_DIR}/src/ggml-metal/libggml-metal.dylib")
        set(GGML_HAS_METAL ON)
        message(STATUS "GGML Metal backend found")
    else()
        set(GGML_HAS_METAL OFF)
    endif()
    set(GGML_HAS_CUDA OFF)
endif()
```

Puis, pour **chacune** des 4 libs GGML (`text_tokenizer`, `tts_transformer`,
`audio_tokenizer_encoder`, `audio_tokenizer_decoder`), ajoute après le bloc `if(GGML_HAS_METAL) ... endif()` :

```cmake
if(GGML_HAS_CUDA)
    target_link_libraries(<NOM_DE_LA_LIB> PUBLIC ggml-cuda)
endif()
```

Avec `add_subdirectory`, les `target_link_libraries(... ggml ggml-base ggml-cpu)` existants se
résolvent comme cibles CMake (les `target_link_directories(... ${GGML_BUILD_DIR}/src ...)` deviennent
inutiles mais inoffensifs).

Build du projet :
```powershell
cmake -S . -B build -DQWEN3_TTS_CUDA=ON -DCMAKE_CUDA_ARCHITECTURES=86 -DCMAKE_BUILD_TYPE=Release -DQWEN3_TTS_TIMING=ON
cmake --build build --config Release -j
```

### Option B (fallback) — linker la lib pré-construite du §3

Si `add_subdirectory` coince (versions ggml capricieuses), garde le build pré-construit du §3 et
ajoute juste le linkage. Détection (après le bloc Metal) :

```cmake
option(QWEN3_TTS_CUDA "Link the prebuilt GGML CUDA backend" OFF)
if(QWEN3_TTS_CUDA)
    set(GGML_HAS_CUDA ON)
else()
    set(GGML_HAS_CUDA OFF)
endif()
```

Pour chacune des 4 libs : ajoute le dossier trouvé au §3 dans `target_link_directories(...)`
(ex. `${GGML_BUILD_DIR}/src/ggml-cuda`) puis :
```cmake
if(GGML_HAS_CUDA)
    target_link_libraries(<NOM_DE_LA_LIB> PUBLIC ggml-cuda)
endif()
```
Build : `cmake -S . -B build -DQWEN3_TTS_CUDA=ON -DCMAKE_BUILD_TYPE=Release -DQWEN3_TTS_TIMING=ON` puis `cmake --build build --config Release -j`.

> `⚠️ À VALIDER` — runtime DLLs. Copie `ggml*.dll` + `ggml-cuda.dll` (et au besoin
> `cudart64_12.dll`, `cublas64_12.dll`, `cublasLt64_12.dll` depuis `CUDA\v12.x\bin`)
> **à côté** de `qwen3-tts-cli.exe` et de `qwen3tts.dll` (sous `build\Release\`).

---

## 5. Confirmer que le GPU est bien utilisé

Au lancement, chaque composant logue son backend sur stderr. On veut voir **CUDA**, pas CPU/BLAS :

```powershell
.\build\Release\qwen3-tts-cli.exe -m .\models -t "Bonjour, ceci est un test." -o test.wav
# Attendu sur stderr :  "TTSTransformer backend: CUDA0"   (et non "BLAS" / "CPU")
```

Si tu vois `BLAS`/`CPU` → la lib CUDA n'est pas dans la closure de linkage (revois §4) ou
les DLLs ne sont pas trouvées (revois §4 runtime DLLs).

---

## 6. Bench RTF (le gate)

Lance le harness (voir `bench-rtf.ps1` à côté de ce fichier) :

```powershell
.\docs\tts\bench-rtf.ps1 `
  -Exe .\build\Release\qwen3-tts-cli.exe `
  -ModelDir .\models `
  -TextFile .\docs\tts\bench-text-fr.txt `
  -Runs 5
```

Il imprime, par run : temps de synthèse, durée audio, **RTF = synth / audio**, puis le **RTF médian**.

### Critères GO / NO-GO

| Résultat (RTF médian, 0.6B F16, backend CUDA0) | Décision |
|---|---|
| **≤ 0.7** | ✅ **GO franc** — marge pour le streaming par phrase + 1.7B. On passe à la Phase 1 (P/Invoke + `Qwen3TtsService`). |
| **0.7 – 1.0** | ✅ **GO** pour le 0.6B en lecture live (découpage par phrase). 1.7B probablement > 1.0 → 0.6B seulement. |
| **1.0 – 1.5** | 🟠 **Conditionnel** — tester Q8_0, vérifier que le code-predictor tourne bien sur GPU (pas le stub CPU). Sinon repli. |
| **> 1.5** | 🔴 **NO-GO** sur cette voie — repli **Orpheus** (GGUF + LLamaSharp + SNAC), déjà éprouvé en .NET. |

> Mesure aussi la **VRAM** en parallèle (`nvidia-smi -l 1`) — le 0.6B F16 doit rester ~1-2 Go,
> donc aucun souci sur 24 Go, et de la place pour le LLM + l'ASR de VoxMind.

---

## Après le gate (rappel des phases suivantes)

- **Phase 1** : wrapper P/Invoke de `qwen3tts.dll` (`qwen3_tts_create` / `_synthesize` /
  `_synthesize_with_voice_samples` / `_extract_embedding_file`) → `Qwen3TtsService : ITtsService`,
  `SynthesizeStreamAsync` pipeliné **par phrase** (l'API C est batch → on segmente côté C#).
- **Phase 2** (optionnel) : étendre l'API C pour émettre les frames du décodeur causal (vrai streaming).
- **Licence** : `qwen3-tts.cpp` n'a **aucune licence** (`license: null`) → bloquant si VoxMind est commercial.
  Contacter l'auteur, ou traiter ce port comme implémentation de référence.

---

## 7. Variante CPU-only (machines sans GPU)

Identique au §3 mais **sans** CUDA ni patch CMake — ggml en CPU pur :

```powershell
cmake -S ggml -B ggml/build-cpu -DGGML_CUDA=OFF -DGGML_METAL=OFF -DBUILD_SHARED_LIBS=ON -DCMAKE_BUILD_TYPE=Release
cmake --build ggml/build-cpu --config Release -j
cmake -S . -B build-cpu -DCMAKE_BUILD_TYPE=Release -DQWEN3_TTS_TIMING=ON   # GGML_DIR/build pointé sur build-cpu
cmake --build build-cpu --config Release -j
```

> Sur CPU, le RTF sera nettement plus élevé qu'en CUDA (probablement > 1.0 pour le 0.6B selon le CPU) :
> cette variante sert surtout de **repli fonctionnel**. Le tiering VoxMind garde alors **Kokoro** par défaut.

---

## 8. Déposer les binaires pour VoxMind (.NET)

VoxMind charge la lib native via `VoxMind.Qwen3Tts` (P/Invoke). Copier les sorties de build dans le repo
VoxMind sous **`native/qwen3tts/<os>-<arch>-<backend>/`** (cf. `native/qwen3tts/README.md`) :

```
native/qwen3tts/
├── win-x64-cpu/    qwen3tts.dll  ggml.dll  ggml-base.dll  ggml-cpu.dll
└── win-x64-cuda/   qwen3tts.dll  ggml.dll  ggml-base.dll  ggml-cpu.dll  ggml-cuda.dll
                    + cudart64_12.dll  cublas64_12.dll  cublasLt64_12.dll   (si absents de l'hôte)
```

Le `.csproj` de `VoxMind.Qwen3Tts` copie cette arborescence en sortie ; `Qwen3NativeLibraryResolver` choisit la
variante (`auto` → GPU détecté = cuda, sinon cpu ; ou `cuda`/`cpu` forcé) et ajoute le dossier au `PATH` pour
résoudre les `ggml*.dll` co-localisés.

Modèles GGUF → **`models/qwen3-tts/`** (pas dans `native/`) : `qwen3-tts-0.6b-f16.gguf` +
`qwen3-tts-tokenizer-f16.gguf` (noms attendus par la lib native ; configurables via `Qwen3Config`).

---

## 9. Configuration VoxMind (`config.json` → `ml.tts.qwen3`)

Clés principales (défauts dans `Qwen3Config`) :

| Clé | Défaut | Rôle |
|---|---|---|
| `enabled` | `true` | Enregistre le moteur dans la registry. |
| `backend` | `auto` | `auto` (GPU→cuda, sinon cpu) \| `cuda` \| `cpu`. |
| `model_dir` | `models/qwen3-tts` | Dossier des GGUF (passé à `qwen3_tts_create`). |
| `model_file_name` | `qwen3-tts-0.6b-f16.gguf` | Mettre le **1.7B** ici après le gate RTF. |
| `default_language` | `fr` | Langue si non précisée par la requête. |
| `num_threads` / `temperature` / `top_p` / `top_k` / `repetition_penalty` / `max_audio_tokens` | 8 / 0.9 / 1.0 / 50 / 1.05 / 4096 | Paramètres de génération. |
| `reference_voices` | `{}` | Voix par défaut clonée par langue (WAV 24 kHz mono → embedding caché). |

**Mapping `language_id`** (confirmé depuis `src/main.cpp` du port, 10 langues) :
`en=2050, de=2053, es=2054, zh=2055, ja=2058, fr=2061, ko=2064, ru=2069, it=2070, pt=2071`.

**Tiering** : si Qwen3 est chargé en **CUDA**, il devient le moteur TTS **par défaut** (`/v1/audio/speech` sans
`model`). Sinon Kokoro reste défaut. On force toujours un moteur via `model` dans le body
(`"model": "qwen3"` ou `"model": "kokoro"`).

**Dégradation** : lib native ou GGUF absents → `qwen3` `IsLoaded=false`, `model=qwen3` répond **503**, le reste
de VoxMind (Kokoro) fonctionne.
