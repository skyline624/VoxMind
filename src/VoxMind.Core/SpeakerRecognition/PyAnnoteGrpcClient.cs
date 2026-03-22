using System.Runtime.InteropServices;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using VoxMind.Grpc;

namespace VoxMind.Core.SpeakerRecognition;

public class PyAnnoteGrpcClient : IPyAnnoteClient
{
    private readonly GrpcChannel _channel;
    private readonly VoxMind.Grpc.SpeakerRecognition.SpeakerRecognitionClient _client;
    private readonly ILogger<PyAnnoteGrpcClient> _logger;
    private bool _disposed;

    public PyAnnoteGrpcClient(string endpoint, ILogger<PyAnnoteGrpcClient> logger)
    {
        _logger = logger;
        // Connexion HTTP non-TLS pour communication locale
        var channelOptions = new GrpcChannelOptions
        {
            MaxReceiveMessageSize = 50 * 1024 * 1024,  // 50 MB
            MaxSendMessageSize = 10 * 1024 * 1024       // 10 MB
        };
        _channel = GrpcChannel.ForAddress($"http://{endpoint}", channelOptions);
        _client = new VoxMind.Grpc.SpeakerRecognition.SpeakerRecognitionClient(_channel);
    }

    public async Task<EmbeddingResult> ExtractEmbeddingAsync(byte[] audioData, CancellationToken ct = default)
    {
        try
        {
            var request = new AudioData
            {
                AudioData_ = Google.Protobuf.ByteString.CopyFrom(audioData),
                SampleRate = 16000f,
                DurationMs = audioData.Length / (16000 * 2 / 1000)
            };

            var deadline = DateTime.UtcNow.AddSeconds(10);
            var response = await _client.ExtractEmbeddingAsync(request, deadline: deadline, cancellationToken: ct);

            if (!response.Success)
            {
                _logger.LogWarning("PyAnnote ExtractEmbedding a échoué: {Error}", response.Error);
                return new EmbeddingResult { Success = false, Error = response.Error };
            }

            // Désérialiser bytes → float[]
            var embBytes = response.Embedding.ToByteArray();
            var embedding = MemoryMarshal.Cast<byte, float>(embBytes).ToArray();

            return new EmbeddingResult
            {
                Success = true,
                Embedding = embedding,
                DurationUsed = response.DurationUsed
            };
        }
        catch (global::Grpc.Core.RpcException ex) when (ex.StatusCode == global::Grpc.Core.StatusCode.DeadlineExceeded)
        {
            _logger.LogError("PyAnnote timeout (10s) lors de l'extraction d'embedding");
            return new EmbeddingResult { Success = false, Error = "Timeout (10s)" };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de l'extraction d'embedding PyAnnote");
            return new EmbeddingResult { Success = false, Error = ex.Message };
        }
    }

    public async Task<ComparisonResult> CompareEmbeddingsAsync(float[] embedding1, float[] embedding2, CancellationToken ct = default)
    {
        try
        {
            var emb1Bytes = MemoryMarshal.AsBytes(embedding1.AsSpan()).ToArray();
            var emb2Bytes = MemoryMarshal.AsBytes(embedding2.AsSpan()).ToArray();

            var request = new CompareRequest
            {
                Embedding1 = Google.Protobuf.ByteString.CopyFrom(emb1Bytes),
                Embedding2 = Google.Protobuf.ByteString.CopyFrom(emb2Bytes)
            };

            var response = await _client.CompareEmbeddingsAsync(request, cancellationToken: ct);

            return new ComparisonResult
            {
                CosineSimilarity = response.CosineSimilarity,
                EuclideanDistance = response.EuclideanDistance,
                IsSameSpeaker = response.IsSameSpeaker
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la comparaison d'embeddings");
            throw;
        }
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            var response = await _client.PingAsync(new Empty(), deadline: deadline, cancellationToken: ct);
            return response.Alive;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PyAnnote Ping a échoué");
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _channel.Dispose();
    }
}
