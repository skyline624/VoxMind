using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace VoxMind.Core.RemoteClients;

public class RemoteClientRegistry : IRemoteClientRegistry, IDisposable
{
    private readonly ConcurrentDictionary<string, RemoteClientInfo> _clients = new();
    private readonly TimeSpan _heartbeatTimeout;
    private readonly ILogger<RemoteClientRegistry> _logger;
    private readonly Timer _expiryTimer;
    private bool _disposed;

    public RemoteClientRegistry(ILogger<RemoteClientRegistry> logger, int heartbeatTimeoutSeconds = 30)
    {
        _logger = logger;
        _heartbeatTimeout = TimeSpan.FromSeconds(heartbeatTimeoutSeconds);
        // Vérification des clients expirés toutes les 10 secondes
        _expiryTimer = new Timer(RemoveExpiredClients, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    public void Register(RemoteClientInfo client)
    {
        _clients[client.ClientId] = client;
        _logger.LogInformation("Client distant enregistré : '{Name}' (ID={Id}, Plateforme={Platform})",
            client.Name, client.ClientId, client.Platform);
    }

    public void Unregister(string clientId)
    {
        if (_clients.TryRemove(clientId, out var client))
        {
            client.CommandChannel.Writer.TryComplete();
            _logger.LogInformation("Client distant désinscrit : '{Name}' (ID={Id})", client.Name, clientId);
        }
    }

    public void UpdateHeartbeat(string clientId)
    {
        if (_clients.TryGetValue(clientId, out var client))
            client.LastHeartbeatAt = DateTime.UtcNow;
    }

    public bool Exists(string clientId) => _clients.ContainsKey(clientId);

    public RemoteClientInfo? Get(string clientId) =>
        _clients.TryGetValue(clientId, out var client) ? client : null;

    public RemoteClientInfo? GetByName(string name) =>
        _clients.Values.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<RemoteClientInfo> GetAll() => _clients.Values.ToList();

    private void RemoveExpiredClients(object? state)
    {
        var cutoff = DateTime.UtcNow - _heartbeatTimeout;
        foreach (var (id, client) in _clients)
        {
            if (client.LastHeartbeatAt < cutoff)
            {
                if (_clients.TryRemove(id, out _))
                {
                    client.CommandChannel.Writer.TryComplete();
                    _logger.LogWarning("Client distant expiré (timeout heartbeat) : '{Name}' (ID={Id})",
                        client.Name, id);
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _expiryTimer.Dispose();
        foreach (var client in _clients.Values)
            client.CommandChannel.Writer.TryComplete();
    }
}
