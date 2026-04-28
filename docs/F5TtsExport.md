# Export ONNX des checkpoints F5-TTS

VoxMind utilise les modèles ONNX produits par le port communautaire
[`DakeQQ/F5-TTS-ONNX`](https://github.com/DakeQQ/F5-TTS-ONNX). Cet export
est une opération **one-shot** Python+PyTorch faite côté machine de
développement ; le runtime VoxMind ne dépend que de ONNX Runtime (zéro
Python en production).

## Vue d'ensemble

Pour chaque langue, on doit produire 4 fichiers à déposer dans
`models/f5-tts/<lang>/` :

- `F5_Preprocess.onnx`
- `F5_Transformer.onnx`
- `F5_Decode.onnx`
- `tokens.txt`
- `reference.wav` (audio de référence par défaut, PCM 24 kHz mono ≤ 30 s)

Plus une transcription textuelle de `reference.wav` à mettre dans
`appsettings.json` (`TtsConfig.Languages[lang].DefaultReferenceText`).

## Anglais (base SWivid)

```bash
# 1. Cloner les deux repos
git clone https://github.com/SWivid/F5-TTS.git
git clone https://github.com/DakeQQ/F5-TTS-ONNX.git
cd F5-TTS-ONNX

# 2. Installer les dépendances Python (recommandé : venv ou conda)
python -m venv .venv && source .venv/bin/activate
pip install -r requirements.txt
pip install f5-tts  # tire le checkpoint base depuis HuggingFace

# 3. Lancer l'export EN
cd Export_ONNX/F5_TTS
python Export_F5_TTS.py  # produit les 3 .onnx dans le dossier output

# 4. Récupérer le tokens.txt depuis le checkpoint base
cp ~/.cache/huggingface/hub/models--SWivid--F5-TTS/snapshots/*/F5TTS_v1_Base/vocab.txt \
   /chemin/vers/VoxMind/models/f5-tts/en/tokens.txt

# 5. Déposer les outputs
mv output/F5_Preprocess.onnx  /chemin/vers/VoxMind/models/f5-tts/en/
mv output/F5_Transformer.onnx /chemin/vers/VoxMind/models/f5-tts/en/
mv output/F5_Decode.onnx      /chemin/vers/VoxMind/models/f5-tts/en/

# 6. Audio de référence (~5 s d'une voix EN nette, PCM 24 kHz mono)
ffmpeg -i votre-audio.wav -ar 24000 -ac 1 -sample_fmt s16 \
       /chemin/vers/VoxMind/models/f5-tts/en/reference.wav
```

## Français (fine-tune RASPIAUDIO)

Même pipeline, mais on télécharge le checkpoint communautaire FR avant l'export :

```bash
cd F5-TTS-ONNX/Export_ONNX/F5_TTS

# 1. Télécharger le checkpoint FR
huggingface-cli download RASPIAUDIO/F5-French-MixedSpeakers-reduced \
    --local-dir ./fr-checkpoint

# 2. Adapter le chemin du checkpoint dans Export_F5_TTS.py
#    (variable `model_path` ou équivalent — voir le script)

# 3. Lancer l'export
python Export_F5_TTS.py

# 4. Déposer les outputs et le tokens.txt FR
mkdir -p /chemin/vers/VoxMind/models/f5-tts/fr
mv output/F5_Preprocess.onnx  /chemin/vers/VoxMind/models/f5-tts/fr/
mv output/F5_Transformer.onnx /chemin/vers/VoxMind/models/f5-tts/fr/
mv output/F5_Decode.onnx      /chemin/vers/VoxMind/models/f5-tts/fr/
cp fr-checkpoint/vocab.txt    /chemin/vers/VoxMind/models/f5-tts/fr/tokens.txt

# 5. Voix de référence française (5 s, PCM 24 kHz mono)
ffmpeg -i ma-voix-fr.wav -ar 24000 -ac 1 -sample_fmt s16 \
       /chemin/vers/VoxMind/models/f5-tts/fr/reference.wav
```

## Vérification

Une fois les fichiers en place :

```bash
ls models/f5-tts/fr models/f5-tts/en
# Doit montrer : F5_Preprocess.onnx, F5_Transformer.onnx, F5_Decode.onnx, tokens.txt, reference.wav

# Test rapide CLI
./voxmind speak "Bonjour" --language fr --output /tmp/fr.wav
./voxmind speak "Hello"   --language en --output /tmp/en.wav

# Test API (Api en cours d'exécution)
curl -X POST http://localhost:8000/v1/audio/speech \
  -H "X-Api-Key: $API_KEY" -H "Content-Type: application/json" \
  -d '{"input":"Bonjour le monde.","language":"fr"}' \
  -o /tmp/api-fr.wav

curl http://localhost:8000/v1/voices -H "X-Api-Key: $API_KEY" | jq
# Doit lister fr + en avec is_loaded:true
```

## Notes

- L'export FR depuis RASPIAUDIO est le plus délicat car le script `Export_F5_TTS.py` du repo DakeQQ vise par défaut le base SWivid. Il faut éventuellement adapter le chemin de checkpoint et la config `tokens.txt` (RASPIAUDIO peut utiliser un vocabulaire augmenté).
- Si l'export FR pose problème, fallback en runtime : VoxMind active uniquement EN dans la registry, le détecteur de langue logge un warning quand il bascule.
- La taille des trois ONNX par langue tourne autour de 1.5 GB (FP32) ou 750 MB (FP16). À mettre dans `.gitignore` ; on les distribue via HuggingFace ou un bucket privé selon les choix d'opération.
- Pour un fine-tune custom de la voix Seren, voir le repo upstream [`SWivid/F5-TTS`](https://github.com/SWivid/F5-TTS) section *Training* — chantier dédié, hors périmètre de cette intégration.
