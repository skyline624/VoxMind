using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace VoxMind.Chatterbox;

/// <summary>
/// Portage C# du pipeline d'inférence Chatterbox (onnx-community/chatterbox-multilingual-ONNX).
/// speech_encoder -> boucle [embed_tokens -> language_model (+KV-cache) -> argmax|sampling] -> conditional_decoder.
/// Variante q4. Reproduit fidèlement le script Python de référence.
///
/// Deux modes de décodage du language_model :
///   - <b>greedy</b> (argmax + repetition penalty) : stable sur CPU, comportement par défaut.
///   - <b>sampling</b> (repetition penalty -> softmax(logits/temp) -> top-k -> tirage) : OBLIGATOIRE
///     sur GPU/CUDA, où le greedy déraille (génère 1001 tokens de babillage sans jamais émettre de STOP).
///     Tirage déterministe via <see cref="System.Random"/> seedé à 0 (reproductibilité).
/// </summary>
public sealed class ChatterboxPipeline : IDisposable
{
    private readonly InferenceSession _spk, _emb, _lm, _dec;
    private readonly ChatterboxTokenizer _tok;
    private readonly bool _useSampling;
    private readonly float _temperature;
    private readonly int _topK;
    private readonly bool _kvF16;   // KV-cache float16 (modèles q4f16/fp16) vs float32 (q4) — détecté à la construction
    private readonly bool _cuda;    // device CUDA → décodage IOBinding (KV gardé sur le GPU) ; sinon NamedOnnxValue (CPU)
    private Random _rng = new(0);
    private const int NL = 30, NKV = 16, HD = 64, HIDDEN = 1024;
    private const long START_SPEECH = 6561, STOP_SPEECH = 6562;

    /// <param name="device"><c>"cpu"</c> (défaut) ou <c>"cuda"</c> — sur "cuda", le CUDAExecutionProvider est
    /// ajouté aux sessions (nécessite le package <c>Microsoft.ML.OnnxRuntime.Gpu</c> et le runtime CUDA/cuDNN).</param>
    /// <param name="useSampling"><c>false</c> (défaut) = greedy/argmax ; <c>true</c> = sampling (requis sur GPU).</param>
    /// <param name="temperature">Température du softmax (branche sampling). Plus bas = plus conservateur. <c>0.3</c> par défaut.</param>
    /// <param name="topK">Top-k (branche sampling). Plus bas = plus conservateur. <c>20</c> par défaut.</param>
    public ChatterboxPipeline(string onnxDir, string tokenizerJson, string lmVariant = "q4",
                              string device = "cpu", bool useSampling = false,
                              float temperature = 0.3f, int topK = 20)
    {
        _useSampling = useSampling;
        _temperature = temperature;
        _topK = topK;
        _cuda = string.Equals(device, "cuda", StringComparison.OrdinalIgnoreCase);
        var so = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        // LM autorégressif batch=1 : limiter l'oversubscription des threads (machine à 128 coeurs logiques)
        var soLm = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        soLm.IntraOpNumThreads = 8;
        if (string.Equals(device, "cuda", StringComparison.OrdinalIgnoreCase))
        {
#if CUDA
            // Provider CUDA sur device 0 (RTX 3090). AppendExecutionProvider_CUDA n'existe que dans le
            // package Microsoft.ML.OnnxRuntime.Gpu (build -p:UseCuda=true) ; il n'est résolu au runtime
            // que si la lib native onnxruntime_providers_cuda est présente (package .Gpu + CUDA/cuDNN).
            so.AppendExecutionProvider_CUDA(0);
            soLm.AppendExecutionProvider_CUDA(0);
#else
            // Build CPU (package Microsoft.ML.OnnxRuntime sans support CUDA) : on ne casse pas, on reste
            // sur le CPUExecutionProvider et on signale que la demande "cuda" est ignorée.
            Console.Error.WriteLine(
                "[ChatterboxPipeline] device=\"cuda\" demandé mais cette build n'a pas le support CUDA " +
                "(recompiler avec -p:UseCuda=true et le package Microsoft.ML.OnnxRuntime.Gpu). " +
                "Repli sur le CPUExecutionProvider.");
#endif
        }
        _spk = new InferenceSession(Path.Combine(onnxDir, "speech_encoder.onnx"), so);
        _emb = new InferenceSession(Path.Combine(onnxDir, "embed_tokens.onnx"), soLm);
        string lmFile = lmVariant == "fp32" ? "language_model.onnx" : $"language_model_{lmVariant}.onnx";
        _lm = new InferenceSession(Path.Combine(onnxDir, lmFile), soLm);
        _dec = new InferenceSession(Path.Combine(onnxDir, "conditional_decoder.onnx"), so);
        _tok = new ChatterboxTokenizer(tokenizerJson);
        // Le KV-cache est float16 pour les variantes fp16/q4f16, float32 pour q4. inputs_embeds,
        // attention_mask et logits restent float32 dans tous les cas. Détecté depuis la signature ONNX.
        _kvF16 = _lm.InputMetadata["past_key_values.0.key"].ElementType == typeof(Float16);
    }

    /// <summary>
    /// Exécute le speech_encoder sur l'audio de référence et met en cache ses sorties (features de
    /// conditioning, tokens de prompt, embeddings + features de locuteur). Réutilisable pour plusieurs
    /// appels <see cref="Generate(string,string,ChatterboxReference,float,int)"/> afin de n'exécuter
    /// le speech_encoder qu'une fois par voix de référence (ex. découpage multi-segments).
    /// </summary>
    public ChatterboxReference EncodeReference(float[] refAudio)
    {
        var audioT = new DenseTensor<float>(refAudio, new[] { 1, refAudio.Length });
        using var o = _spk.Run(new[] { NamedOnnxValue.CreateFromTensor("audio_values", audioT) });
        return new ChatterboxReference
        {
            CondEmb = Clone(Get<float>(o, "audio_features")),
            PromptTokens = Get<long>(o, "audio_tokens").ToArray(),
            SpeakerEmb = Clone(Get<float>(o, "speaker_embeddings")),
            SpeakerFeat = Clone(Get<float>(o, "speaker_features")),
        };
    }

    /// <summary>Synthèse à partir d'un audio de référence brut (encode la référence puis génère).</summary>
    public float[] Generate(string text, string lang, float[] refAudio, float exaggeration = 0.5f, int maxNew = 1000)
        => Generate(text, lang, EncodeReference(refAudio), exaggeration, maxNew);

    /// <summary>Synthèse à partir d'une voix de référence déjà encodée (voir <see cref="EncodeReference"/>).</summary>
    public float[] Generate(string text, string lang, ChatterboxReference reference, float exaggeration = 0.5f, int maxNew = 1000)
    {
        // Sampling reproductible : on repart de la même graine à chaque appel.
        if (_useSampling) _rng = new Random(0);

        // 1) speech_encoder (pré-calculé) : conditioning audio + tokens + embeddings/features locuteur.
        var condEmb = reference.CondEmb;
        var promptTokens = reference.PromptTokens;
        var speakerEmb = reference.SpeakerEmb;
        var speakerFeat = reference.SpeakerFeat;

        // 2) tokenisation + position_ids
        long[] ids = _tok.Encode(text, lang);
        long[] pos = new long[ids.Length];
        for (int i = 0; i < ids.Length; i++) pos[i] = ids[i] >= START_SPEECH ? 0 : i - 1;

        // 3) décodage autorégressif. Sur GPU (CUDA) avec KV float16 : OrtIoBinding gardant le KV-cache
        //    sur le device entre les tokens (supprime le marshalling managed↔native + l'aller-retour
        //    GPU↔CPU du KV par token). Sinon (CPU, ou cuda+q4 float32) : NamedOnnxValue (inchangé).
        //    Les deux remplissent `gen` à l'identique (mêmes logits, même sélection de token).
        var gen = (_cuda && _kvF16)
            ? DecodeCuda(condEmb, ids, pos, exaggeration, maxNew)
            : DecodeCpu(condEmb, ids, pos, exaggeration, maxNew);

        // 4) speech_tokens = [prompt_tokens, gen[1..^1]]
        var st = new List<long>(promptTokens);
        for (int i = 1; i < gen.Count - 1; i++) st.Add(gen[i]);
        var stT = new DenseTensor<long>(st.ToArray(), new[] { 1, st.Count });

        // 4) conditional_decoder
        using var dout = _dec.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("speech_tokens", stT),
            NamedOnnxValue.CreateFromTensor("speaker_embeddings", speakerEmb),
            NamedOnnxValue.CreateFromTensor("speaker_features", speakerFeat),
        });
        return Get<float>(dout, "waveform").ToArray();
    }

    /// <summary>
    /// Décodage autorégressif via NamedOnnxValue (chemin CPU, et repli GPU sans IOBinding).
    /// Le KV-cache suit le type du modèle : <see cref="Float16"/> (fp16/q4f16) ou <c>float</c> (q4),
    /// détecté par <see cref="_kvF16"/>. Pour q4 (<c>_kvF16=false</c>) le comportement est STRICTEMENT
    /// identique à l'origine. inputs_embeds, attention_mask et logits restent float32 dans tous les cas.
    /// Les `present` du pas N deviennent les `past` du pas N+1 sans copie (vues sur les sorties ORT,
    /// gardées vivantes via <c>prevOut</c> jusqu'au pas suivant).
    /// </summary>
    private List<long> DecodeCpu(DenseTensor<float> condEmb, long[] ids, long[] pos, float exaggeration, int maxNew)
    {
        var gen = new List<long> { START_SPEECH };
        var exagT = new DenseTensor<float>(new[] { exaggeration }, new[] { 1 });
        var past = new object[NL * 2];
        for (int i = 0; i < past.Length; i++) past[i] = _kvF16
            ? new DenseTensor<Float16>(Array.Empty<Float16>(), new[] { 1, NKV, 0, HD })
            : (object)new DenseTensor<float>(Array.Empty<float>(), new[] { 1, NKV, 0, HD });
        DenseTensor<long> curIds = new(ids, new[] { 1, ids.Length });
        DenseTensor<long> curPos = new(pos, new[] { 1, ids.Length });
        DenseTensor<long> attn = null!;
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue>? prevOut = null;

        for (int step = 0; step < maxNew; step++)
        {
            DenseTensor<float> ie;
            using (var eo = _emb.Run(new[]
            {
                NamedOnnxValue.CreateFromTensor("input_ids", curIds),
                NamedOnnxValue.CreateFromTensor("position_ids", curPos),
                NamedOnnxValue.CreateFromTensor("exaggeration", exagT),
            }))
                ie = Clone(Get<float>(eo, "inputs_embeds"));
            if (step == 0) { ie = ConcatSeq(condEmb, ie); attn = Ones(ie.Dimensions[1]); }

            var lmIn = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("inputs_embeds", ie),
                NamedOnnxValue.CreateFromTensor("attention_mask", attn),
            };
            for (int l = 0; l < NL; l++)
            {
                lmIn.Add(KvIn($"past_key_values.{l}.key", past[2 * l]));
                lmIn.Add(KvIn($"past_key_values.{l}.value", past[2 * l + 1]));
            }

            var curOut = _lm.Run(lmIn);                       // NON disposé ici : ses `present` servent de `past` au pas suivant
            var arr = curOut.ToArray();                        // [0]=logits, [1+2l]=present.l.key, [2+2l]=present.l.value
            var logits = arr[0].AsTensor<float>();
            int tLast = logits.Dimensions[1] - 1, vSize = logits.Dimensions[2];
            long next = _useSampling
                ? SampleWithPenalty(logits, tLast, vSize, gen, 1.2f, _temperature, _topK)
                : ArgmaxWithPenalty(logits, tLast, vSize, gen, 1.2f);
            gen.Add(next);
            if (next != STOP_SPEECH)
            {
                for (int l = 0; l < NL; l++)
                {
                    past[2 * l] = _kvF16 ? arr[1 + 2 * l].AsTensor<Float16>() : (object)arr[1 + 2 * l].AsTensor<float>();
                    past[2 * l + 1] = _kvF16 ? arr[2 + 2 * l].AsTensor<Float16>() : (object)arr[2 + 2 * l].AsTensor<float>();
                }
            }
            prevOut?.Dispose();
            prevOut = curOut;
            if (next == STOP_SPEECH) break;
            curIds = new DenseTensor<long>(new[] { next }, new[] { 1, 1 });
            curPos = new DenseTensor<long>(new[] { (long)step + 1 }, new[] { 1, 1 });
            attn = Append1(attn);
        }
        prevOut?.Dispose();
        return gen;
    }

    /// <summary>
    /// Décodage autorégressif GPU (CUDA) avec <see cref="OrtIoBinding"/> : le KV-cache float16 reste
    /// SUR LE GPU entre les tokens — les `present` du pas N sont reliés directement comme `past` du
    /// pas N+1 sans copie GPU↔CPU ni marshalling managed↔native. Seuls inputs_embeds/attention_mask
    /// (liés depuis le CPU, copiés une fois par ORT) et les logits (ramenés au CPU pour la sélection
    /// du token) traversent la frontière. Respecte <see cref="_useSampling"/> (sampling requis sur
    /// CUDA — le greedy y déraille), avec EXACTEMENT les mêmes opérateurs de sélection que le chemin
    /// CPU. Réservé au cas device=cuda + KV float16 (cf. dispatch dans <see cref="Generate"/>).
    /// </summary>
    private List<long> DecodeCuda(DenseTensor<float> condEmb, long[] ids, long[] pos, float exaggeration, int maxNew)
    {
        var gen = new List<long> { START_SPEECH };
        using var cudaMem = new OrtMemoryInfo("Cuda", OrtAllocatorType.DeviceAllocator, 0, OrtMemType.Default);
        var cpuMem = OrtMemoryInfo.DefaultInstance;   // singleton partagé — NE PAS disposer
        using var ro = new RunOptions();

        // KV "past" sur le device : départ = tenseurs float16 vides (ORT les matérialise sur le GPU).
        var past = new OrtValue[NL * 2];
        for (int i = 0; i < past.Length; i++)
            past[i] = OrtValue.CreateTensorValueFromMemory(Array.Empty<Float16>(), new long[] { 1, NKV, 0, HD });

        long[] curIds = ids; long[] curPos = pos; int curLen = ids.Length;
        long[] attn = null!;
        try
        {
            for (int step = 0; step < maxNew; step++)
            {
                // embed_tokens -> inputs_embeds float32 (concaténé au conditioning audio au pas 0).
                DenseTensor<float> ie;
                using (var eo = _emb.Run(new[]
                {
                    NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(curIds, new[] { 1, curLen })),
                    NamedOnnxValue.CreateFromTensor("position_ids", new DenseTensor<long>(curPos, new[] { 1, curLen })),
                    NamedOnnxValue.CreateFromTensor("exaggeration", new DenseTensor<float>(new[] { exaggeration }, new[] { 1 })),
                }))
                    ie = Clone(Get<float>(eo, "inputs_embeds"));
                if (step == 0) { ie = ConcatSeq(condEmb, ie); attn = new long[ie.Dimensions[1]]; Array.Fill(attn, 1L); }
                int lmSeq = ie.Dimensions[1];
                float[] ieData = ie.ToArray();

                using var io = _lm.CreateIoBinding();
                using var ieOv = OrtValue.CreateTensorValueFromMemory(ieData, new long[] { 1, lmSeq, HIDDEN });
                using var amOv = OrtValue.CreateTensorValueFromMemory(attn, new long[] { 1, attn.Length });
                io.BindInput("inputs_embeds", ieOv);
                io.BindInput("attention_mask", amOv);
                for (int l = 0; l < NL; l++)
                {
                    io.BindInput($"past_key_values.{l}.key", past[2 * l]);
                    io.BindInput($"past_key_values.{l}.value", past[2 * l + 1]);
                }
                io.BindOutputToDevice("logits", cpuMem);              // logits -> CPU pour la sélection du token
                for (int l = 0; l < NL; l++)
                {
                    io.BindOutputToDevice($"present.{l}.key", cudaMem);   // KV gardé SUR LE GPU
                    io.BindOutputToDevice($"present.{l}.value", cudaMem);
                }
                _lm.RunWithBinding(ro, io);
                var outs = io.GetOutputValues();   // [0]=logits(CPU), [1+2l]=present.l.key(GPU), [2+2l]=present.l.value(GPU)

                // dernière ligne de logits -> tenseur [1,1,V] pour réutiliser les sélecteurs du chemin CPU.
                var logitsSpan = outs[0].GetTensorDataAsSpan<float>();
                int V = logitsSpan.Length / lmSeq;
                int baseIdx = (lmSeq - 1) * V;
                var row = new float[V];
                for (int v = 0; v < V; v++) row[v] = logitsSpan[baseIdx + v];
                var rowT = new DenseTensor<float>(row, new[] { 1, 1, V });
                long next = _useSampling
                    ? SampleWithPenalty(rowT, 0, V, gen, 1.2f, _temperature, _topK)
                    : ArgmaxWithPenalty(rowT, 0, V, gen, 1.2f);
                gen.Add(next);

                // les `present` (GPU) deviennent les `past` du tour suivant — AUCUNE copie GPU↔CPU.
                // on libère d'abord les `past` consommés (Run terminé), puis on récupère les nouveaux.
                for (int i = 0; i < past.Length; i++) past[i].Dispose();
                for (int l = 0; l < NL; l++) { past[2 * l] = outs[1 + 2 * l]; past[2 * l + 1] = outs[2 + 2 * l]; }
                outs[0].Dispose();   // logits (CPU) — consommés

                if (next == STOP_SPEECH) break;
                curIds = new[] { next }; curPos = new[] { (long)step + 1 }; curLen = 1;
                var na = new long[attn.Length + 1]; Array.Copy(attn, na, attn.Length); na[attn.Length] = 1L; attn = na;
            }
        }
        finally
        {
            // libère le KV float16 encore résident sur le GPU (sinon fuite mémoire device par requête).
            for (int i = 0; i < past.Length; i++) past[i]?.Dispose();
        }
        return gen;
    }

    /// <summary>Crée l'entrée KV en respectant le type du tenseur (Float16 pour fp16/q4f16, float pour q4).</summary>
    private static NamedOnnxValue KvIn(string name, object t) => t is Tensor<Float16> h
        ? NamedOnnxValue.CreateFromTensor(name, h)
        : NamedOnnxValue.CreateFromTensor(name, (Tensor<float>)t);

    private static Tensor<T> Get<T>(IReadOnlyCollection<DisposableNamedOnnxValue> o, string name)
        => o.First(x => x.Name == name).AsTensor<T>();

    private static DenseTensor<float> Clone(Tensor<float> t)
        => new(t.ToArray(), t.Dimensions.ToArray());

    private static DenseTensor<float> ConcatSeq(DenseTensor<float> a, DenseTensor<float> b)
    {
        int S = a.Dimensions[1], T = b.Dimensions[1], D = a.Dimensions[2];
        var buf = new float[(S + T) * D];
        a.Buffer.Span.CopyTo(buf);
        b.Buffer.Span.CopyTo(buf.AsSpan(S * D));
        return new DenseTensor<float>(buf, new[] { 1, S + T, D });
    }

    private static DenseTensor<long> Ones(int n)
    {
        var b = new long[n];
        Array.Fill(b, 1L);
        return new DenseTensor<long>(b, new[] { 1, n });
    }

    private static DenseTensor<long> Append1(DenseTensor<long> a)
    {
        int n = a.Dimensions[1];
        var b = new long[n + 1];
        a.Buffer.Span.CopyTo(b);
        b[n] = 1L;
        return new DenseTensor<long>(b, new[] { 1, n + 1 });
    }

    private static long ArgmaxWithPenalty(Tensor<float> logits, int t, int V, List<long> gen, float penalty)
    {
        var s = new float[V];
        for (int v = 0; v < V; v++) s[v] = logits[0, t, v];
        foreach (var g in gen)
        {
            if (g < 0 || g >= V) continue;
            s[(int)g] = s[(int)g] < 0 ? s[(int)g] * penalty : s[(int)g] / penalty;
        }
        int best = 0; float bv = s[0];
        for (int v = 1; v < V; v++) if (s[v] > bv) { bv = s[v]; best = v; }
        return best;
    }

    /// <summary>
    /// Décodage par échantillonnage (reproduit le sampling Python validé sur GPU) :
    /// repetition penalty -> softmax(logits/temp) -> top-k -> tirage pondéré.
    /// Indispensable sur CUDA (le greedy y déraille). Le tirage utilise <see cref="_rng"/> (seed 0).
    /// </summary>
    private long SampleWithPenalty(Tensor<float> logits, int t, int V, List<long> gen,
                                   float penalty = 1.2f, float temp = 0.3f, int topK = 20)
    {
        // 1) repetition penalty (identique au greedy), en float64 comme le POC Python.
        var s = new double[V];
        for (int v = 0; v < V; v++) s[v] = logits[0, t, v];
        foreach (var g in gen)
        {
            if (g < 0 || g >= V) continue;
            s[(int)g] = s[(int)g] < 0 ? s[(int)g] * penalty : s[(int)g] / penalty;
        }
        // 2) softmax(scores / temp) sur tout le vocabulaire.
        double max = double.NegativeInfinity;
        for (int v = 0; v < V; v++) { s[v] /= temp; if (s[v] > max) max = s[v]; }
        double sum = 0;
        for (int v = 0; v < V; v++) { s[v] = Math.Exp(s[v] - max); sum += s[v]; }
        for (int v = 0; v < V; v++) s[v] /= sum;
        // 3) top-k : indices des k plus fortes probabilités.
        int k = Math.Min(topK, V);
        var idx = new int[V];
        for (int v = 0; v < V; v++) idx[v] = v;
        Array.Sort(idx, (a, b) => s[b].CompareTo(s[a]));
        // 4) renormalisation sur le top-k puis tirage pondéré (déterministe via _rng).
        double pkSum = 0;
        for (int i = 0; i < k; i++) pkSum += s[idx[i]];
        double r = _rng.NextDouble() * pkSum;
        double acc = 0;
        for (int i = 0; i < k; i++)
        {
            acc += s[idx[i]];
            if (r <= acc) return idx[i];
        }
        return idx[k - 1];
    }

    public void Dispose()
    {
        _spk.Dispose(); _emb.Dispose(); _lm.Dispose(); _dec.Dispose();
    }
}

/// <summary>
/// Sorties du speech_encoder pré-calculées pour une voix de référence (conditioning audio, tokens
/// de prompt, embeddings et features de locuteur). Produit par <see cref="ChatterboxPipeline.EncodeReference"/>
/// et réutilisable pour plusieurs segments d'un même appel afin de n'exécuter le speech_encoder qu'une fois.
/// Les tenseurs sont des copies (clones) : ils ne sont pas mutés par la génération et sont donc réutilisables.
/// </summary>
public sealed class ChatterboxReference
{
    internal DenseTensor<float> CondEmb { get; init; } = null!;
    internal long[] PromptTokens { get; init; } = Array.Empty<long>();
    internal DenseTensor<float> SpeakerEmb { get; init; } = null!;
    internal DenseTensor<float> SpeakerFeat { get; init; } = null!;
}
