namespace VoxMind.Core.RemoteClients;

public interface IRemoteClientRegistry
{
    void Register(RemoteClientInfo client);
    void Unregister(string clientId);
    void UpdateHeartbeat(string clientId);
    bool Exists(string clientId);
    RemoteClientInfo? Get(string clientId);
    IReadOnlyList<RemoteClientInfo> GetAll();
}
