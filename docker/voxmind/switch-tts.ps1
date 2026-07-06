<#
.SYNOPSIS
  Bascule le moteur TTS de VoxMind entre Qwen3-TTS (voix clonée) et Voxtral-TTS (voix presets).
  Un seul modèle à la fois (chaque pipeline vLLM-omni réserve ~20 Go sur une 24 Go).

.EXAMPLE
  .\switch-tts.ps1 voxtral                 # → Voxtral, voix fr_female
  .\switch-tts.ps1 voxtral -VoxtralVoice fr_male
  .\switch-tts.ps1 qwen3                    # → Qwen3, ta voix clonée (voxmind_clone_xv)

.NOTES
  Recrée le conteneur voxmind-aio avec le bon backend. Le sidecar recharge le modèle (~3-5 min) ;
  pendant ce temps model=qwen3 répond 503, Kokoro reste dispo. Les modèles sont en cache (volume
  voxmind-hf) → pas de re-téléchargement après la première fois.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet("qwen3", "voxtral")]
    [string]$Backend,

    [string]$VoxtralVoice = "fr_female",
    [string]$RepoRoot = "D:/developpement/VoxMind",
    [int]$Port = 8090,
    [string]$Image = "voxmind:aio"
)

$ErrorActionPreference = "Stop"

docker rm -f voxmind-aio 2>$null | Out-Null

$args = @(
    "run", "-d", "--gpus", "all", "-p", "${Port}:8090",
    "-v", "${RepoRoot}/models:/app/models",
    "-v", "${RepoRoot}/voice_data:/app/voice_data",
    "-v", "voxmind-hf:/root/.cache/huggingface",
    "--name", "voxmind-aio", "--restart", "unless-stopped"
)

if ($Backend -eq "voxtral") {
    $args += @("-e", "TTS_BACKEND=voxtral", "-e", "VOXTRAL_VOICE=$VoxtralVoice")
    $desc = "Voxtral-TTS (voix preset « $VoxtralVoice »)"
}
else {
    # Qwen3 en mode clonage (Base) = ta voix clonée. Pour les 9 voix presets, mettre QWEN3_TASK_TYPE=CustomVoice.
    $args += @("-e", "TTS_BACKEND=qwen3",
        "-e", "QWEN3_MODEL=Qwen/Qwen3-TTS-12Hz-1.7B-Base",
        "-e", "QWEN3_TASK_TYPE=Base")
    $desc = "Qwen3-TTS (voix clonée)"
}
$args += $Image

docker @args | Out-Null
Write-Host "→ Bascule sur $desc." -ForegroundColor Green
Write-Host "  Le sidecar recharge le modèle (~3-5 min). Suivi : docker logs -f voxmind-aio" -ForegroundColor DarkGray
