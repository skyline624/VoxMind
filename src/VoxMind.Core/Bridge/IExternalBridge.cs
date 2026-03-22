namespace VoxMind.Core.Bridge;

public interface IExternalBridge : IDisposable
{
    /// <summary>Démarrer le polling des commandes entrantes</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>Arrêter le bridge proprement</summary>
    Task StopAsync();

    /// <summary>Publier le statut système vers le fichier partagé</summary>
    Task PublishStatusAsync(SystemStatus status);
}
