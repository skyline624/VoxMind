using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace VoxMind.Parakeet;

/// <summary>
/// ONNX decoder-joint for Parakeet TDT (decoder_joint-model.int8.onnx).
/// Implements TDT (Token and Duration Transducer) greedy decoding.
///
/// Model inputs (NeMo TDT ONNX export):
///   - encoder_outputs: [1, 1024, 1]   — one encoder frame (int32 hidden=1024)
///   - targets:         [1, 1]          — previous token (int32)
///   - target_length:   [1]             — always 1 (int32)
///   - input_states_1:  [2, 1, 640]     — LSTM hidden state
///   - input_states_2:  [2, 1, 640]     — LSTM cell state
/// Model outputs:
///   - outputs:         [1, 1, 1, vocab] — joint logits
///   - prednet_lengths: [1]
///   - output_states_1: [2, 1, 640]
///   - output_states_2: [2, 1, 640]
///
/// Encoder output is [1, 1024, T'] (NeMo convention: [batch, hidden, time]).
/// Frame t is extracted by column: encoderOutput[h * T' + t] for h in [0, 1024).
/// </summary>
public sealed class ParakeetDecoderJoint : IDisposable
{
    private readonly InferenceSession _session;
    private readonly TokenDecoder _tokenDecoder;
    private readonly string _encoderInputName;
    private readonly string _targetInputName;
    private readonly string _targetLengthInputName;
    private readonly string _inputState1Name;
    private readonly string _inputState2Name;
    private readonly string _logitsOutputName;
    private readonly string _outputState1Name;
    private readonly string _outputState2Name;
    private const int HiddenDim = 1024;
    private const int StateDim = 640;
    private const int MaxTokensPerFrame = 8;

    public ParakeetDecoderJoint(string modelPath, TokenDecoder tokenDecoder, SessionOptions opts)
    {
        _tokenDecoder = tokenDecoder;
        _session = new InferenceSession(modelPath, opts);

        var inputNames = _session.InputMetadata.Keys.ToList();
        var outputNames = _session.OutputMetadata.Keys.ToList();

        _encoderInputName    = inputNames.Count > 0 ? inputNames[0] : "encoder_outputs";
        _targetInputName     = inputNames.Count > 1 ? inputNames[1] : "targets";
        _targetLengthInputName = inputNames.Count > 2 ? inputNames[2] : "target_length";
        _inputState1Name     = inputNames.Count > 3 ? inputNames[3] : "input_states_1";
        _inputState2Name     = inputNames.Count > 4 ? inputNames[4] : "input_states_2";

        _logitsOutputName = outputNames.Count > 0 ? outputNames[0] : "outputs";
        _outputState1Name = outputNames.Count > 2 ? outputNames[2] : "output_states_1";
        _outputState2Name = outputNames.Count > 3 ? outputNames[3] : "output_states_2";
    }

    /// <summary>
    /// TDT greedy decoding over all encoder frames.
    /// Returns decoded token IDs (excluding blank/BOS/EOS).
    /// encoderOutput is in [1, HiddenDim, encodedFrames] layout (NeMo convention).
    /// </summary>
    public int[] DecodeGreedy(float[] encoderOutput, long encodedFrames, int hiddenDim)
    {
        var result = new List<int>();
        int prevToken = _tokenDecoder.BosIndex;
        int blankId   = _tokenDecoder.BlankIndex;
        int eosId     = _tokenDecoder.EosIndex;
        int vocabSize = _tokenDecoder.VocabSize;

        // Initialize LSTM states to zero: [2, 1, 640]
        float[] state1 = new float[2 * 1 * StateDim];
        float[] state2 = new float[2 * 1 * StateDim];

        for (long t = 0; t < encodedFrames; t++)
        {
            for (int step = 0; step < MaxTokensPerFrame; step++)
            {
                float[] logits = RunDecoderStep(
                    encoderOutput, (int)encodedFrames, (int)t,
                    prevToken, state1, state2,
                    out float[] newState1, out float[] newState2);

                state1 = newState1;
                state2 = newState2;

                int token = ArgMax(logits, vocabSize);

                if (token == blankId || token == eosId)
                    break;

                result.Add(token);
                prevToken = token;
            }
        }

        return result.ToArray();
    }

    private float[] RunDecoderStep(
        float[] encoderOutput, int totalFrames, int frameIdx,
        int prevToken,
        float[] state1, float[] state2,
        out float[] newState1, out float[] newState2)
    {
        // Extract frame frameIdx from [1, HiddenDim, T'] encoder output.
        // Element [0, h, frameIdx] is at flat index h * totalFrames + frameIdx.
        var frameSlice = new float[HiddenDim];
        for (int h = 0; h < HiddenDim; h++)
            frameSlice[h] = encoderOutput[h * totalFrames + frameIdx];

        // encoder_outputs: [1, 1024, 1]
        var encoderTensor = new DenseTensor<float>(frameSlice, new[] { 1, HiddenDim, 1 });
        // targets: [1, 1] int32
        var targetTensor = new DenseTensor<int>(new int[] { prevToken }, new[] { 1, 1 });
        // target_length: [1] int32
        var targetLengthTensor = new DenseTensor<int>(new int[] { 1 }, new[] { 1 });
        // input_states: [2, 1, 640]
        var state1Tensor = new DenseTensor<float>(state1, new[] { 2, 1, StateDim });
        var state2Tensor = new DenseTensor<float>(state2, new[] { 2, 1, StateDim });

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_encoderInputName, encoderTensor),
            NamedOnnxValue.CreateFromTensor(_targetInputName, targetTensor),
            NamedOnnxValue.CreateFromTensor(_targetLengthInputName, targetLengthTensor),
            NamedOnnxValue.CreateFromTensor(_inputState1Name, state1Tensor),
            NamedOnnxValue.CreateFromTensor(_inputState2Name, state2Tensor),
        };

        using var results = _session.Run(inputs);

        float[] logits = results.First(r => r.Name == _logitsOutputName).AsTensor<float>().ToArray();
        newState1 = results.First(r => r.Name == _outputState1Name).AsTensor<float>().ToArray();
        newState2 = results.First(r => r.Name == _outputState2Name).AsTensor<float>().ToArray();

        return logits;
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
