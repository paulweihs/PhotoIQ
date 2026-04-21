using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace PhotoIQPro.AI.Engines;

/// <summary>
/// CLIP text encoder for computing text embeddings via ONNX Runtime.
/// </summary>
/// <remarks>
/// Encodes natural language text (tag vocabulary prompts) into 512D vectors in the same space as ClipEngine's image embeddings.
/// This enables cosine similarity comparisons for zero-shot tag classification. The text model accepts token sequences
/// (up to 77 tokens) and returns either pooled embeddings (for classification) or full sequence outputs.
///
/// The model file (clip-vit-base-patch32-text.onnx) is optional; if missing, IsModelAvailable returns false and tagging degrades gracefully.
/// </remarks>
public sealed class ClipTextEngine : IDisposable
{
    private InferenceSession? _session;
    private readonly string _modelPath;
    private string? _inputName;
    private string? _outputName;
    private bool _outputIsPooled;

    /// <summary>
    /// Initializes a new instance of the ClipTextEngine.
    /// </summary>
    /// <remarks>
    /// The ONNX model is not loaded until InitializeAsync is called. The model path is computed but not validated here.
    /// </remarks>
    /// <param name="modelsPath">The directory containing clip-vit-base-patch32-text.onnx.</param>
    public ClipTextEngine(string modelsPath) =>
        _modelPath = Path.Combine(modelsPath, "clip-vit-base-patch32-text.onnx");

    /// <summary>Gets a value indicating whether the ONNX model file exists on disk.</summary>
    /// <remarks>If false, tagging functionality is unavailable but the app continues normally.</remarks>
    public bool IsModelAvailable => File.Exists(_modelPath);
    /// <summary>Gets a value indicating whether the model has been successfully loaded and initialized.</summary>
    public bool IsInitialized => _session != null;

    /// <summary>
    /// Loads and initializes the CLIP text model from the ONNX file.
    /// </summary>
    /// <remarks>
    /// Automatically detects input and output layer names from the model metadata. If the model file does not exist,
    /// silently returns without initializing (for graceful degradation when the text encoder is optional).
    /// Runs on the thread pool to avoid blocking.
    /// </remarks>
    public async Task InitializeAsync()
    {
        if (!File.Exists(_modelPath)) return;

        // InferenceSession() is CPU-bound and can take 5–30 s on first load.
        // Run on the thread pool so the caller is never blocked.
        // Create before swapping so that a constructor or metadata failure leaves the old session intact.
        var newSession = await Task.Run(() => new InferenceSession(_modelPath));
        string? inputName, outputName;
        bool outputIsPooled;
        try
        {
            inputName = newSession.InputMetadata.Keys
                .FirstOrDefault(k => k.Contains("input_ids"))
                ?? newSession.InputMetadata.Keys.FirstOrDefault()
                ?? throw new InvalidOperationException(
                    $"CLIP text model '{_modelPath}' has no input layers. The file may be corrupt or an incompatible version.");

            // Prefer a pre-pooled output (text_embeds / pooler_output) over the full sequence.
            outputName = newSession.OutputMetadata.Keys
                .FirstOrDefault(k => k.Contains("embeds") || k.Contains("pooler"))
                ?? newSession.OutputMetadata.Keys.FirstOrDefault()
                ?? throw new InvalidOperationException(
                    $"CLIP text model '{_modelPath}' has no output layers. The file may be corrupt or an incompatible version.");

            outputIsPooled = newSession.OutputMetadata[outputName].Dimensions.Length == 2;
        }
        catch
        {
            newSession.Dispose();
            throw;
        }

        _session?.Dispose();
        _session = newSession;
        _inputName = inputName;
        _outputName = outputName;
        _outputIsPooled = outputIsPooled;
    }

    /// <summary>
    /// Computes 512D embeddings for a batch of tokenized text sequences.
    /// </summary>
    /// <remarks>
    /// Accepts tokenized text (up to 77 tokens per sequence). Returns embeddings in the same vector space as CLIP image embeddings.
    /// If not initialized or input is empty, returns an empty array.
    /// </remarks>
    /// <param name="tokenBatches">Jagged array of token IDs, one sequence per row (each up to 77 tokens).</param>
    /// <returns>Batch of 512D float arrays, one per input sequence.</returns>
    public Task<float[][]> GetTextEmbeddingsAsync(int[][] tokenBatches)
    {
        if (_session == null || _inputName == null || _outputName == null || tokenBatches.Length == 0)
            return Task.FromResult(Array.Empty<float[]>());

        const int seqLen = 77;
        int batchSize = tokenBatches.Length;

        var idsTensor = new DenseTensor<long>([batchSize, seqLen]);
        for (int b = 0; b < batchSize; b++)
            for (int i = 0; i < seqLen; i++)
                idsTensor[b, i] = i < tokenBatches[b].Length ? tokenBatches[b][i] : 0L;

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_inputName, idsTensor)
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
