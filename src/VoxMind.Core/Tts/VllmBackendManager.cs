using System.IO;
using Microsoft.Extensions.Logging;

namespace VoxMind.Core.Tts;

/// <summary>
/// Gère le <b>backend TTS vLLM actif</b> (<c>qwen3</c> ou <c>voxtral</c>), partagé par les deux instances de
/// <see cref="Qwen3VllmTtsService"/>. Un seul modèle tient sur le GPU → « basculer » = recharger le sidecar.
///
/// <para>VoxMind ne gère AUCUN process : pour demander une bascule, il écrit le backend voulu dans un
/// <b>fichier d'état</b> ; le watcher du conteneur (<c>backend-watch.sh</c>) détecte le changement et recharge
/// le sidecar avec le bon modèle. Le backend actif est relu depuis ce même fichier (source de vérité,
/// survit aux redémarrages).</para>
/// </summary>
public sealed class VllmBackendManager
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SwitchDebounce = TimeSpan.FromSeconds(45);

    private readonly string _stateFile;
    private readonly string _default;
    private readonly ILogger<VllmBackendManager> _logger;
    private readonly object _lock = new();

    private string _cached;
    private DateTime _cachedAt = DateTime.MinValue;
    private DateTime _lastSwitch = DateTime.MinValue;

    public VllmBackendManager(string stateFile, string defaultBackend, ILogger<VllmBackendManager> logger)
    {
        _stateFile = stateFile;
        _default = Normalize(defaultBackend) is { Length: > 0 } d ? d : "qwen3";
        _cached = _default;
        _logger = logger;
    }

    /// <summary>Backend actuellement servi par le sidecar (lu du fichier d'état, caché ~2 s).</summary>
    public string ActiveBackend
    {
        get
        {
            lock (_lock)
            {
                if (DateTime.UtcNow - _cachedAt < CacheTtl)
                    return _cached;
                try
                {
                    if (File.Exists(_stateFile))
                    {
                        var v = Normalize(File.ReadAllText(_stateFile));
                        if (v.Length > 0)
                            _cached = v;
                    }
                }
                catch { /* garde la dernière valeur connue */ }
                _cachedAt = DateTime.UtcNow;
                return _cached;
            }
        }
    }

    /// <summary>
    /// Demande la bascule vers <paramref name="target"/> : écrit le fichier d'état → le watcher recharge le
    /// sidecar (~3 min). No-op si déjà actif ; debouncé pour éviter les reloads en rafale.
    /// </summary>
    public void RequestSwitch(string target)
    {
        target = Normalize(target);
        if (target.Length == 0)
            return;
        lock (_lock)
        {
            if (target == ActiveBackend)
                return;
            if (DateTime.UtcNow - _lastSwitch < SwitchDebounce)
            {
                _logger.LogInformation("Bascule TTS vers « {Target} » déjà demandée récemment (debounce).", target);
                return;
            }
            try
            {
                var dir = Path.GetDirectoryName(_stateFile);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(_stateFile, target);
                _lastSwitch = DateTime.UtcNow;
                _cached = target;                 // le sidecar va recharger ; l'actif visé est déjà « target »
                _cachedAt = DateTime.UtcNow;
                _logger.LogWarning("Bascule TTS demandée → « {Target} » : rechargement du sidecar (~3 min).", target);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Échec de l'écriture du fichier d'état de backend « {File} ».", _stateFile);
            }
        }
    }

    private static string Normalize(string? s) => (s ?? string.Empty).Trim().ToLowerInvariant();
}
