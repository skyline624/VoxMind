using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace VoxMind.Parakeet;

/// <summary>
/// ONNX decoder-joint for Parakeet TDT (decoder_joint-model.int8.onnx).
/// Implements TDT (Token and Duration Transducer) greedy decoding.
///
/// Model input assumptions (NeMo TDT ONNX export):
///   - encoder_output: [1, 1, hidden_dim] — one encoder frame
///   - targets:        [1, 1]             — previous token (int64)
/// Model output:
///   - logits: [1, 1, vocab_size] — token probabilities
///
/// Note: Input/output names are read from model metadata.
/// If model uses different shapes, adjust slicing in DecodeGreedy.
/// </summary>
public sealed class ParakeetDecoderJoint : IDisposable
{
    private readonly InferenceSession _session;
    private readonly TokenDecoder _tokenDecoder;
    private readonly string _encoderInputName;
    private readonly string _targetInputName;
    private readonly string _logitsOutputName;
    private const int MaxTokensPerFrame = 8;

    public ParakeetDecoderJoint(string modelPath, TokenDecoder tokenDecoder)
    {
        _tokenDecoder = tokenDecoder;
        var opts = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        _session = new InferenceSession(modelPath, opts);

        var inputNames = _session.InputMetadata.Keys.ToList();
        var outputNames = _session.OutputMetadata.Keys.ToList();

        // Typical names: encoder_output, targets → logits
        _encoderInputName = inputNames.Count > 0 ? inputNames[0] : "encoder_output";
        _targetInputName = inputNames.Count > 1 ? inputNames[1] : "targets";
        _logitsOutputName = outputNames.Count > 0 ? outputNames[0] : "logits";
    }

    /// <summary>
    /// TDT greedy decoding over all encoder frames.
    /// Returns decoded token IDs (excluding blank/BOS/EOS).
    /// </summary>
    public int[] DecodeGreedy(float[] encoderOutput, long encodedFrames, int hiddenDim)
    {
        var result = new List<int>();
        int prevToken = _tokenDecoder.BosIndex;
        int blankId = _tokenDecoder.BlankIndex;
        int eosId = _tokenDecoder.EosIndex;
        int vocabSize = _tokenDecoder.VocabSize;

        for (long t = 0; t < encodedFrames; t++)
        {
            // Extract encoder output at frame t: [1, 1, hiddenDim]
            float[] frameSlice = new float[hiddenDim];
            Array.Copy(encoderOutput, (int)t * hiddenDim, frameSlice, 0, hiddenDim);

            // Greedy decode at this encoder frame (up to MaxTokensPerFrame to avoid loops)
            for (int step = 0; step < MaxTokensPerFrame; step++)
            {
                float[] logits = RunDecoderStep(frameSlice, hiddenDim, prevToken);
                int token = ArgMax(logits, vocabSize);

                if (token == blankId || token == eosId)
                    break; // advance to next encoder frame

                result.Add(token);
                prevToken = token;
            }
        }

        return result.ToArray();
    }

    private float[] RunDecoderStep(float[] frameSlice, int hiddenDim, int prevToken)
    {
        // encoder_output: [1, 1, hiddenDim]
        var encoderTensor = new DenseTensor<float>(frameSlice, new[] { 1, 1, hiddenDim });
        // targets: [1, 1]
        var targetTensor = new DenseTensor<long>(new long[] { prevToken }, new[] { 1, 1 });

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_encoderInputName, encoderTensor),
            NamedOnnxValue.CreateFromTensor(_targetInputName, targetTensor)
        };

        using var results = _session.Run(inputs);
        return results.First(r => r.Name == _logitsOutputName).AsTensor<float>().ToArray();
    }

    private static int ArgMax(float[] logits, int vocabSize)
    {
        int count = Math.Min(logits.Length, vocabSize);
        int maxIdx = 0;
        float maxVal = float.NegativeInfinity;
        for (int i = 0; i < count; i++)
        {
            if (logits[i] > maxVal)
            {
                maxVal = logits[i];
                maxIdx = i;
            }
        }
        return maxIdx;
    }

    public void Dispose() => _session.Dispose();
}
