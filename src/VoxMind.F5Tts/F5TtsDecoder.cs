using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace VoxMind.F5Tts;

/// <summary>
/// Décodeur F5-TTS (<c>F5_Decode.onnx</c>) — vocoder Vocos 24 kHz.
///
/// Contrat (port DakeQQ) :
/// - mel: <c>float32 [1, L, D]</c> — mel-spectrogram produit par le transformer.
/// Sortie : <c>float32 [1, n_samples]</c> — audio PCM 24 kHz mono normalisé [-1, 1].
/// </summary>
public sealed class F5TtsDecoder : IDisposable
{
    public const int SampleRate = 24000;

    private readonly InferenceSession _session;
    private readonly string _melInputName;
    private readonly string _audioOutputName;

    public F5TtsDecoder(string modelPath, SessionOptions opts)
    {
        _session = new InferenceSession(modelPath, opts);
        var inputs = _session.InputMetadata.Keys.ToList();
        var outputs = _session.OutputMetadata.Keys.ToList();

        _melInputName = inputs[0];
        _audioOutputName = outputs[0];
    }

    public float[] Decode(DenseTensor<float> mel)
    {
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_melInputName, mel),
        };

        using var results = _session.Run(inputs);
        var audio = results.First(r => r.Name == _audioOutputName).AsTensor<float>();
        return audio.ToArray();
    }

    public void Dispose() => _session.Dispose();
}
