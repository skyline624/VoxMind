using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace VoxMind.F5Tts;

/// <summary>
/// Préprocesseur F5-TTS (<c>F5_Preprocess.onnx</c>).
///
/// Contrat (port DakeQQ/F5-TTS-ONNX) — les noms d'I/O sont lus depuis la
/// metadata ONNX au runtime pour rester tolérant aux ré-exports :
/// - audio: <c>float32 [1, n_samples]</c> — voix de référence PCM 24 kHz mono normalisé.
/// - prompt_ids: <c>int32 [1, P]</c> — token IDs de la transcription de la voix de référence.
/// - target_ids: <c>int32 [1, T]</c> — token IDs du texte à synthétiser.
/// Sortie principale : embeddings <c>[1, L, D]</c> à transmettre au transformer.
/// </summary>
public sealed class F5TtsPreprocessor : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _audioInputName;
    private readonly string _promptInputName;
    private readonly string _targetInputName;
    private readonly string _outputName;

    public F5TtsPreprocessor(string modelPath, SessionOptions opts)
    {
        _session = new InferenceSession(modelPath, opts);

        var inputNames = _session.InputMetadata.Keys.ToList();
        var outputNames = _session.OutputMetadata.Keys.ToList();

        if (inputNames.Count < 3)
            throw new InvalidOperationException(
                $"F5_Preprocess.onnx attend au moins 3 entrées (audio, prompt_ids, target_ids), trouvé {inputNames.Count}.");

        _audioInputName = inputNames[0];
        _promptInputName = inputNames[1];
        _targetInputName = inputNames[2];
        _outputName = outputNames[0];
    }

    public DenseTensor<float> Run(float[] referencePcm24kHz, int[] promptIds, int[] targetIds)
    {
        var audioTensor = new DenseTensor<float>(referencePcm24kHz, new[] { 1, referencePcm24kHz.Length });
        var promptTensor = new DenseTensor<int>(promptIds, new[] { 1, promptIds.Length });
        var targetTensor = new DenseTensor<int>(targetIds, new[] { 1, targetIds.Length });

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_audioInputName, audioTensor),
            NamedOnnxValue.CreateFromTensor(_promptInputName, promptTensor),
            NamedOnnxValue.CreateFromTensor(_targetInputName, targetTensor),
        };

        using var results = _session.Run(inputs);
        var output = results.First(r => r.Name == _outputName).AsTensor<float>();

        // Copie pour ne pas dépendre du buffer ORT après Dispose
        var dims = output.Dimensions.ToArray();
        var data = output.ToArray();
        return new DenseTensor<float>(data, dims);
    }

    public void Dispose() => _session.Dispose();
}
