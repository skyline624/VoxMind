using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace VoxMind.F5Tts;

/// <summary>
/// Transformeur DiT F5-TTS (<c>F5_Transformer.onnx</c>).
///
/// F5-TTS est un modèle de flow-matching : on part d'un bruit gaussien et on
/// l'intègre vers le mel cible via N étapes Euler. À chaque étape, on appelle
/// le transformer avec (état courant, embeddings de conditionnement, t).
///
/// Contrat (port DakeQQ) — toujours 3 entrées dans l'ordre standard :
/// - x: <c>float32 [1, L, D]</c> — état de flow courant.
/// - cond: <c>float32 [1, L, D]</c> — embeddings issus du préprocesseur.
/// - t: <c>float32 [1]</c> — temps normalisé dans [0, 1].
/// Sortie : prédiction de vélocité <c>[1, L, D]</c> à intégrer.
/// </summary>
public sealed class F5TtsTransformer : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _xInputName;
    private readonly string _condInputName;
    private readonly string _timeInputName;
    private readonly string _outputName;

    public F5TtsTransformer(string modelPath, SessionOptions opts)
    {
        _session = new InferenceSession(modelPath, opts);
        var inputs = _session.InputMetadata.Keys.ToList();
        var outputs = _session.OutputMetadata.Keys.ToList();

        if (inputs.Count < 3)
            throw new InvalidOperationException(
                $"F5_Transformer.onnx attend 3 entrées (x, cond, t), trouvé {inputs.Count}.");

        _xInputName = inputs[0];
        _condInputName = inputs[1];
        _timeInputName = inputs[2];
        _outputName = outputs[0];
    }

    /// <summary>
    /// Intègre le flow-matching sur N étapes Euler.
    /// Renvoie le mel-spectrogram cible <c>[1, L, D]</c> à transmettre au décodeur.
    /// </summary>
    public DenseTensor<float> Sample(DenseTensor<float> conditioning, int numSteps)
    {
        if (numSteps <= 0) throw new ArgumentOutOfRangeException(nameof(numSteps));

        // x_0 ~ N(0, 1)
        var dims = conditioning.Dimensions.ToArray();
        int total = 1;
        foreach (var d in dims) total *= d;

        var rng = new Random(42);
        var x = new DenseTensor<float>(dims);
        for (int i = 0; i < total; i++)
        {
            // Box-Muller pour un échantillon gaussien standard
            double u1 = 1.0 - rng.NextDouble();
            double u2 = 1.0 - rng.NextDouble();
            x.Buffer.Span[i] = (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
        }

        float dt = 1.0f / numSteps;

        for (int step = 0; step < numSteps; step++)
        {
            float t = step * dt;
            var velocity = RunStep(x, conditioning, t);

            // Euler : x ← x + dt * v
            for (int i = 0; i < total; i++)
                x.Buffer.Span[i] += dt * velocity.Buffer.Span[i];
        }

        return x;
    }

    private DenseTensor<float> RunStep(DenseTensor<float> x, DenseTensor<float> cond, float t)
    {
        var tTensor = new DenseTensor<float>(new[] { t }, new[] { 1 });

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_xInputName, x),
            NamedOnnxValue.CreateFromTensor(_condInputName, cond),
            NamedOnnxValue.CreateFromTensor(_timeInputName, tTensor),
        };

        using var results = _session.Run(inputs);
        var velocity = results.First(r => r.Name == _outputName).AsTensor<float>();

        var dims = velocity.Dimensions.ToArray();
        var data = velocity.ToArray();
        return new DenseTensor<float>(data, dims);
    }

    public void Dispose() => _session.Dispose();
}
