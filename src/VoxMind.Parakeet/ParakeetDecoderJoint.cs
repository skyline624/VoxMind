using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace VoxMind.Parakeet;

/// <summary>
/// ONNX decoder-joint for Parakeet TDT (decoder_joint-model.int8.onnx).
/// Implements TDT (Token-and-Duration Transducer) greedy decoding.
/// </summary>
/// <remarks>
/// TDT predicts, at each step, a token AND a <b>duration</b> = how many encoder frames to advance.
/// The joint output (<c>outputs</c>) is the token logits with the duration logits CONCATENATED on
/// the tail: <c>[vocab(8193) | durations(5)]</c> for parakeet-v3. The time loop MUST advance by the
/// predicted duration; decoding it as a plain RNN-T (fixed 1-frame steps, durations ignored)
/// misaligns the emission and drops/merges syllables ("indépendant" → "indant") — the original bug.
/// The prediction-network state is committed only when a non-blank token is emitted (a blank must
/// not advance the predictor). If TDT yields nothing we fall back to a plain RNN-T pass so a decode
/// quirk can never silently drop the whole utterance.
/// </remarks>
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
    private readonly ILogger? _logger;
    private bool _loggedShape;
    private const int HiddenDim = 1024;
    private const int StateDim = 640;
    // Per-frame symbol cap: with duration 0 the loop stays on the same frame to emit another token;
    // bound it so a degenerate run can't spin forever on one frame.
    private const int MaxSymbolsPerFrame = 10;
    // TDT durations are small ([0,1,2,3,4] for parakeet-v3). A wildly larger tail means the output
    // isn't laid out as we expect, so we fall back to a safe 1-frame advance.
    private const int MaxPlausibleDurations = 16;

    public ParakeetDecoderJoint(string modelPath, TokenDecoder tokenDecoder, SessionOptions opts, ILogger? logger = null)
    {
        _tokenDecoder = tokenDecoder;
        _logger = logger;
        _session = new InferenceSession(modelPath, opts);

        var inputNames = _session.InputMetadata.Keys.ToList();
        var outputNames = _session.OutputMetadata.Keys.ToList();

        _encoderInputName = inputNames.Count > 0 ? inputNames[0] : "encoder_outputs";
        _targetInputName = inputNames.Count > 1 ? inputNames[1] : "targets";
        _targetLengthInputName = inputNames.Count > 2 ? inputNames[2] : "target_length";
        _inputState1Name = inputNames.Count > 3 ? inputNames[3] : "input_states_1";
        _inputState2Name = inputNames.Count > 4 ? inputNames[4] : "input_states_2";

        // Output 0 = joint logits with the durations concatenated on the tail; the two state tensors
        // are the LAST two outputs. (Output 1, "prednet_lengths", is a length scalar — NOT durations.)
        _logitsOutputName = outputNames.Count > 0 ? outputNames[0] : "outputs";
        _outputState1Name = outputNames.Count > 2 ? outputNames[^2] : "output_states_1";
        _outputState2Name = outputNames.Count > 1 ? outputNames[^1] : "output_states_2";

        if (_logger is not null)
        {
            foreach (var name in outputNames)
            {
                var dims = _session.OutputMetadata[name].Dimensions;
                _logger.LogInformation(
                    "Parakeet decoder_joint output '{Name}': [{Dims}]", name, string.Join(",", dims));
            }
        }
    }

    public int[] DecodeGreedy(float[] encoderOutput, long encodedFrames, int hiddenDim)
    {
        var tokens = DecodeTdt(encoderOutput, encodedFrames);
        if (tokens.Length == 0 && encodedFrames > 0)
        {
            _logger?.LogWarning("Parakeet: TDT decode produced no tokens; falling back to an RNN-T pass.");
            tokens = DecodeRnnt(encoderOutput, encodedFrames);
        }

        return tokens;
    }

    /// <summary>Canonical TDT greedy: emit one token per joint call, advance time by the predicted duration.</summary>
    private int[] DecodeTdt(float[] encoderOutput, long encodedFrames)
    {
        var result = new List<int>();
        int prevToken = _tokenDecoder.BosIndex;
        int blankId = _tokenDecoder.BlankIndex;
        int eosId = _tokenDecoder.EosIndex;
        int vocabSize = _tokenDecoder.VocabSize;

        float[] state1 = new float[2 * StateDim];
        float[] state2 = new float[2 * StateDim];

        long t = 0;
        int symbolsAtFrame = 0;
        long step = 0;
        long maxSteps = (encodedFrames + 1) * (MaxSymbolsPerFrame + 1) + 16;

        // Diagnostics: how the decode actually behaved (so an empty/garbled run is explainable).
        int blanks = 0;
        var durHist = new int[8];
        float maxNonBlankLogit = float.NegativeInfinity;

        while (t < encodedFrames && step++ < maxSteps)
        {
            float[] logits = RunDecoderStep(
                encoderOutput, (int)encodedFrames, (int)t,
                prevToken, state1, state2,
                out float[] newState1, out float[] newState2);

            int durationCount = logits.Length - vocabSize;
            int token = ArgMax(logits, 0, vocabSize);
            int duration = durationCount is > 0 and <= MaxPlausibleDurations
                ? ArgMax(logits, vocabSize, durationCount) // durations [0,1,2,…] → argmax index IS the frame count
                : 1;

            if (!_loggedShape && _logger is not null)
            {
                _loggedShape = true;
                _logger.LogInformation(
                    "Parakeet TDT: logits len={Logits}, vocab={Vocab}, durationCount={Durations} (first token={Token}, dur={Dur}).",
                    logits.Length, vocabSize, durationCount, token, duration);
            }

            durHist[Math.Clamp(duration, 0, durHist.Length - 1)]++;
            var emitted = token != blankId && token != eosId;
            if (emitted)
            {
                if (logits[token] > maxNonBlankLogit) { maxNonBlankLogit = logits[token]; }
                result.Add(token);
                prevToken = token;
                // Commit the prediction-network state ONLY on a real emission (a blank must not
                // advance the predictor — the previous bug advanced it on every step).
                state1 = newState1;
                state2 = newState2;
                symbolsAtFrame++;
            }
            else
            {
                blanks++;
            }

            if (duration > 0)
            {
                t += duration;
                symbolsAtFrame = 0;
            }
            else if (!emitted || symbolsAtFrame >= MaxSymbolsPerFrame)
            {
                // Duration 0 = stay to emit another symbol; force progress on a blank or at the cap.
                t += 1;
                symbolsAtFrame = 0;
            }
        }

        _logger?.LogInformation(
            "Parakeet TDT summary: encFrames={Frames}, steps={Steps}, blanks={Blanks}, emitted={Emitted}, durHist=[{Hist}], maxEmitLogit={Max:F2}.",
            encodedFrames, step, blanks, result.Count, string.Join(",", durHist), maxNonBlankLogit);

        return [.. result];
    }

    /// <summary>Plain RNN-T greedy (advance one frame, emit up to N tokens per frame). Safety fallback.</summary>
    private int[] DecodeRnnt(float[] encoderOutput, long encodedFrames)
    {
        var result = new List<int>();
        int prevToken = _tokenDecoder.BosIndex;
        int blankId = _tokenDecoder.BlankIndex;
        int eosId = _tokenDecoder.EosIndex;
        int vocabSize = _tokenDecoder.VocabSize;

        float[] state1 = new float[2 * StateDim];
        float[] state2 = new float[2 * StateDim];

        for (long t = 0; t < encodedFrames; t++)
        {
            for (int step = 0; step < MaxSymbolsPerFrame; step++)
            {
                float[] logits = RunDecoderStep(
                    encoderOutput, (int)encodedFrames, (int)t,
                    prevToken, state1, state2,
                    out float[] newState1, out float[] newState2);

                state1 = newState1;
                state2 = newState2;

                int token = ArgMax(logits, 0, vocabSize);
                if (token == blankId || token == eosId)
                {
                    break;
                }

                result.Add(token);
                prevToken = token;
            }
        }

        return [.. result];
    }

    private float[] RunDecoderStep(
        float[] encoderOutput, int totalFrames, int frameIdx,
        int prevToken,
        float[] state1, float[] state2,
        out float[] newState1, out float[] newState2)
    {
        var frameSlice = new float[HiddenDim];
        for (int h = 0; h < HiddenDim; h++)
        {
            frameSlice[h] = encoderOutput[h * totalFrames + frameIdx];
        }

        var encoderTensor = new DenseTensor<float>(frameSlice, [1, HiddenDim, 1]);
        var targetTensor = new DenseTensor<int>(new int[] { prevToken }, [1, 1]);
        var targetLengthTensor = new DenseTensor<int>(new int[] { 1 }, [1]);
        var state1Tensor = new DenseTensor<float>(state1, [2, 1, StateDim]);
        var state2Tensor = new DenseTensor<float>(state2, [2, 1, StateDim]);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_encoderInputName, encoderTensor),
            NamedOnnxValue.CreateFromTensor(_targetInputName, targetTensor),
            NamedOnnxValue.CreateFromTensor(_targetLengthInputName, targetLengthTensor),
            NamedOnnxValue.CreateFromTensor(_inputState1Name, state1Tensor),
            NamedOnnxValue.CreateFromTensor(_inputState2Name, state2Tensor),
        };

        using var results = _session.Run(inputs);

        float[] logits = [.. results.First(r => r.Name == _logitsOutputName).AsTensor<float>()];
        newState1 = [.. results.First(r => r.Name == _outputState1Name).AsTensor<float>()];
        newState2 = [.. results.First(r => r.Name == _outputState2Name).AsTensor<float>()];

        return logits;
    }

    private static int ArgMax(float[] values, int offset, int count)
    {
        int n = Math.Min(count, values.Length - offset);
        int maxIdx = 0;
        float maxVal = float.NegativeInfinity;
        for (int i = 0; i < n; i++)
        {
            if (values[offset + i] > maxVal)
            {
                maxVal = values[offset + i];
                maxIdx = i;
            }
        }
        return maxIdx;
    }

    public void Dispose() => _session.Dispose();
}
