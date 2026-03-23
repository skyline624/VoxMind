using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using VoxMind.Grpc;
using VoxMind.ClientLite.ClientServices;
using VoxMind.ClientLite.ClientServices.AudioCapture;
using VoxMind.ClientLite.Configuration;

namespace VoxMind.ClientLite;

internal class Program
{
    static async Task Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        var config = LoadConfiguration(args);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Console.WriteLine("\nArrêt du client VoxMind...");
        };

        // Construire le host d'abord pour accéder à la DI
        var host = Host.CreateDefaultBuilder(args)
            .UseSerilog()
            .ConfigureServices(services =>
            {
                services.AddSingleton(config);
                services.AddSingleton(_ => GrpcChannel.ForAddress(config.ServerAddress));
                services.AddSingleton(sp =>
                    new VoxMindClientService.VoxMindClientServiceClient(
                        sp.GetRequiredService<GrpcChannel>()));
                services.AddSingleton<ClientRegistration>();
                services.AddSingleton<LiveAudioCapture>();
                services.AddSingleton<ServerCommandHandler>();
                services.AddHostedService<HeartbeatService>();
                services.AddHostedService<AudioStreamClient>();
            })
            .Build();

        // Inscription auprès du serveur avant de démarrer les services
        var registration = host.Services.GetRequiredService<ClientRegistration>();
        if (!await registration.RegisterAsync(cts.Token))
        {
            Log.Fatal("Impossible de s'inscrire auprès du serveur {Address}. Arrêt.", config.ServerAddress);
            Log.CloseAndFlush();
            return;
        }

        try
        {
            await host.RunAsync(cts.Token);
        }
        finally
        {
            await registration.UnregisterAsync();
            var channel = host.Services.GetRequiredService<GrpcChannel>();
            await channel.ShutdownAsync();
            Log.CloseAndFlush();
        }
    }

    private static ClientConfiguration LoadConfiguration(string[] args)
    {
        var config = new ClientConfiguration();

        for (int i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--server": config.ServerAddress = args[i + 1]; break;
                case "--name": config.ClientName = args[i + 1]; break;
                case "--token": config.SharedToken = args[i + 1]; break;
            }
        }

        if (Environment.GetEnvironmentVariable("VOXMIND_SERVER") is { } srv)
            config.ServerAddress = srv;
        if (Environment.GetEnvironmentVariable("VOXMIND_TOKEN") is { } tok)
            config.SharedToken = tok;
        if (Environment.GetEnvironmentVariable("VOXMIND_CLIENT_NAME") is { } name)
            config.ClientName = name;

        return config;
    }
}
