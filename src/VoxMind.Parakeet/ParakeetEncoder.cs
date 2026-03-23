using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace VoxMind.Parakeet;

/// <summary>
/// ONNX encoder for Parakeet TDT model (encoder-model.int8.onnx).
/// Input:  mel features [1, 128, T] + length [1]
/// Output: encoder hidden states [1, T', hidden_dim] + encoded lengths [1]
///
/// Note: Tensor names are read from model metadata at construction.
/// </summary>
public sealed class ParakeetEncoder : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _audioInputName;
    private readonly string _lengthInputName;
    private readonly string _encoderOutputName;
    private readonly string _encodedLengthName;
    private readonly bool _lengthIsInt64;
    private const int NMels = 128;

    public ParakeetEncoder(string modelPath)
    {
        var opts = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        _session = new InferenceSession(modelPath, opts);

        var inputNames = _session.InputMetadata.Keys.ToList();
        var outputNames = _session.OutputMetadata.Keys.ToList();

        _audioInputName = inputNames[0];
        _lengthInputName = inputNames.Count > 1 ? inputNames[1] : inputNames[0];
        _encoderOutputName = outputNames[0];
        _encodedLengthName = outputNames.Count > 1 ? outputNames[1] : outputNames[0];

        _lengthIsInt64 = _session.InputMetadata[_lengthInputName].ElementType == typeof(long);
    }

    /// <summary>
    /// Encode mel features into hidden states.
    /// Returns (encoder output flat array, encoded frame count, hidden dimension per frame).
    /// </summary>
    public (float[] Output, long EncodedFrames, int HiddenDim) Encode(float[] melFeatures, long melFrames)
    {
        // mel shape: [1, 128, melFrames]
        var melTensor = new DenseTensor<float>(melFeatures, new[] { 1, NMels, (int)melFrames });

        NamedOnnxValue lengthInput;
        if (_lengthIsInt64)
        {
            var lt = new DenseTensor<long>(new[] { melFrames }, new[] { 1 });
            lengthInput = NamedOnnxValue.CreateFromTensor(_lengthInputName, lt);
        }
        else
        {
            var lt = new DenseTensor<int>(new[] { (int)melFrames }, new[] { 1 });
            lengthInput = NamedOnnxValue.CreateFromTensor(_lengthInputName, lt);
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_audioInputName, melTensor),
            lengthInput
        };

        using var results = _session.Run(inputs);

        var encoderTensor = results.First(r => r.Name == _encoderOutputName).AsTensor<float>();
        var lenTensor = results.First(r => r.Name == _encodedLengthName).AsTensor<long>();

        long encodedFrames = lenTensor[0];
        float[] output = encoderTensor.ToArray();

        // output shape: [1, encodedFrames, hiddenDim]
        int hiddenDim = encodedFrames > 0 ? output.Length / (int)encodedFrames : 1;

        return (output, encodedFrames, hiddenDim);
    }

    public void Dispose() => _session.Dispose();
}
