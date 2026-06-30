<#
.SYNOPSIS
    Bench RTF (Real-Time Factor) de qwen3-tts.cpp pour le gate Phase 0.
    RTF = temps_de_synthese / duree_audio. Objectif lecture live : RTF <= 1.0.

.EXAMPLE
    .\bench-rtf.ps1 -Exe .\build\Release\qwen3-tts-cli.exe -ModelDir .\models -TextFile .\docs\tts\bench-text-fr.txt -Runs 5
#>
param(
    [string]$Exe      = ".\build\Release\qwen3-tts-cli.exe",
    [string]$ModelDir = ".\models",
    [string]$TextFile = ".\docs\tts\bench-text-fr.txt",
    [int]   $Runs     = 5
)

if (-not (Test-Path $Exe))      { throw "Executable introuvable : $Exe" }
if (-not (Test-Path $ModelDir)) { throw "Dossier modeles introuvable : $ModelDir" }
if (-not (Test-Path $TextFile)) { throw "Fichier texte introuvable : $TextFile" }

function Get-WavDurationSeconds([string]$path) {
    $b = [System.IO.File]::ReadAllBytes($path)
    if ($b.Length -lt 44) { return 0 }
    $pos = 12  # saute "RIFF" + size + "WAVE"
    $byteRate = 0; $dataSize = 0
    while ($pos + 8 -le $b.Length) {
        $id   = [System.Text.Encoding]::ASCII.GetString($b, $pos, 4)
        $size = [BitConverter]::ToUInt32($b, $pos + 4)
        if ($id -eq 'fmt ') {
            # corps fmt : audioFormat(2) numChannels(2) sampleRate(4) byteRate(4) ...
            $byteRate = [BitConverter]::ToUInt32($b, $pos + 8 + 8)
        } elseif ($id -eq 'data') {
            $dataSize = $size
            break
        }
        $pos += 8 + $size + ($size % 2)   # chunks alignes sur 2 octets
    }
    if ($byteRate -eq 0) { return 0 }
    return [double]$dataSize / [double]$byteRate
}

$text   = (Get-Content -Raw -Encoding UTF8 $TextFile).Trim()
$tmpOut = Join-Path $env:TEMP "qwen3_bench.wav"

Write-Host "== Warmup (chargement modeles + 1ere passe, non chronometree) ==" -ForegroundColor Cyan
$warmLog = & $Exe -m $ModelDir -t $text -o $tmpOut 2>&1
$backendLine = $warmLog | Select-String -Pattern 'backend:' | Select-Object -First 1
if ($backendLine) {
    $isGpu = $backendLine -match 'CUDA|Metal|GPU'
    $color = if ($isGpu) { 'Green' } else { 'Red' }
    Write-Host ("Backend detecte : {0}" -f $backendLine.ToString().Trim()) -ForegroundColor $color
    if (-not $isGpu) { Write-Host "  /!\ Backend NON-GPU : le RTF ci-dessous est CPU. Revois le §4/§5 de la recette." -ForegroundColor Red }
} else {
    Write-Host "  (ligne 'backend:' non trouvee dans les logs — build sans -DQWEN3_TTS_TIMING ?)" -ForegroundColor Yellow
}

$rtfs = @()
Write-Host "`n== $Runs runs chronometres ==" -ForegroundColor Cyan
for ($i = 1; $i -le $Runs; $i++) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    & $Exe -m $ModelDir -t $text -o $tmpOut 2>&1 | Out-Null
    $sw.Stop()
    $dur  = Get-WavDurationSeconds $tmpOut
    $wall = $sw.Elapsed.TotalSeconds
    if ($dur -le 0) { Write-Host ("Run {0}: WAV illisible, ignore" -f $i) -ForegroundColor Yellow; continue }
    $rtf = $wall / $dur
    $rtfs += $rtf
    Write-Host ("Run {0}: synth {1,6:N2}s | audio {2,6:N2}s | RTF {3:N3}" -f $i, $wall, $dur, $rtf)
}

if ($rtfs.Count -gt 0) {
    $sorted = $rtfs | Sort-Object
    $median = $sorted[[int][math]::Floor($sorted.Count / 2)]
    $min    = $sorted[0]
    Write-Host ""
    $verdict = if ($median -le 1.0) { "GO (<= 1.0)" } else { "NO-GO (> 1.0)" }
    $vcolor  = if ($median -le 1.0) { 'Green' } else { 'Red' }
    Write-Host ("RTF median : {0:N3}  | meilleur : {1:N3}  => {2}" -f $median, $min, $verdict) -ForegroundColor $vcolor
    Write-Host "Rappel : RTF <= 1.0 = synthese plus rapide que la lecture (live possible)."
} else {
    Write-Host "Aucune mesure exploitable." -ForegroundColor Red
}
