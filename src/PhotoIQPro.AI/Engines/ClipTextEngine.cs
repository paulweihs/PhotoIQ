using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace PhotoIQPro.AI.Engines;

public sealed class ClipTextEngine : IDisposable
{
    private InferenceSession? _session;
    private readonly string _modelPath;
    private string? _inputName;
    private string? _outputName;
    private bool _outputIsPooled;

    public ClipTextEngine(string modelsPath) =>
        _modelPath = Path.Combine(modelsPath, "clip-vit-base-patch32-text.onnx");

    public bool IsModelAvailable => File.Exists(_modelPath);
    public bool IsInitialized => _session != null;

    public Task InitializeAsync()
    {
        if (!File.Exists(_modelPath)) return Task.CompletedTask;

        _session = new InferenceSession(_modelPath);

        _inputName = _session.InputMetadata.Keys
            .FirstOrDefault(k => k.Contains("input_ids"))
            ?? _session.InputMetadata.Keys.First();

        // Prefer a pre-pooled output (text_embeds / pooler_output) over the full sequence.
        _outputName = _session.OutputMetadata.Keys
            .FirstOrDefault(k => k.Contains("embeds") || k.Contains("pooler"))
            ?? _session.OutputMetadata.Keys.First();

        _outputIsPooled = _session.OutputMetadata[_outputName!].Dimensions.Length == 2;

        return Task.CompletedTask;
    }

    public Task<float[][]> GetTextEmbeddingsAsync(int[][] tokenBatches)
    {
        if (_session == null || tokenBatches.Length == 0)
            return Task.FromResult(Array.Empty<float[]>());

        const int seqLen = 77;
        int batchSize = tokenBatches.Length;

        var idsTensor = new DenseTensor<long>([batchSize, seqLen]);
        for (int b = 0; b < batchSize; b++)
            for (int i = 0; i < seqLen; i++)
                idsTensor[b, i] = i < tokenBatches[b].Length ? tokenBatches[b][i] : 0L;

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_inputName!, idsTensor)
        };

        if (_session.InputMetadata.ContainsKey("attention_mask"))
        {
            var maskTensor = new DenseTensor<long>([batchSize, seqLen]);
            for (int b = 0; b < batchSize; b++)
                for (int i = 0; i < seqLen; i++)
                    maskTensor[b, i] = tokenBatches[b][i] != 0 ? 1L : 0L;
            inputs.Add(NamedOnnxValue.CreateFromTensor("attention_mask", maskTensor));
        }

        using var results = _session.Run(inputs);
        var outputTensor = results.First(r => r.Name == _outputName).AsTensor<float>();

        var embeddings = new float[batchSize][];

        if (_outputIsPooled)
        {
            // Output is [batch, dim] — already pooled.
            int dim = outputTensor.Dimensions[1];
            for (int b = 0; b < batchSize; b++)
            {
                var emb = new float[dim];
                for (int i = 0; i < dim; i++) emb[i] = outputTensor[b, i];
                Normalize(emb);
                embeddings[b] = emb;
            }
        }
        else
        {
            // Output is [batch, seq, dim] — pool at the EOS token position.
            const int eosToken = 49407;
            int dim = outputTensor.Dimensions[2];
            for (int b = 0; b < batchSize; b++)
            {
                int eosPos = seqLen - 1;
                for (int i = 1; i < seqLen; i++)
                    if (tokenBatches[b][i] == eosToken) { eosPos = i; break; }

                var emb = new float[dim];
                for (int i = 0; i < dim; i++) emb[i] = outputTensor[b, eosPos, i];
                Normalize(emb);
                embeddings[b] = emb;
            }
        }

        return Task.FromResult(embeddings);
    }

    private static void Normalize(float[] v)
    {
        float norm = MathF.Sqrt(v.Sum(x => x * x));
        if (norm > 1e-8f)
            for (int i = 0; i < v.Length; i++) v[i] /= norm;
    }

    public void Dispose() => _session?.Dispose();
}
