using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhotoIQPro.AI.Engines;

/// <summary>
/// CLIP vision model encoder for computing image embeddings via ONNX Runtime.
/// </summary>
/// <remarks>
/// CLIP (Contrastive Language-Image Pre-training) embeds images and text into a 512D shared vector space,
/// enabling zero-shot classification and semantic search. This engine handles:
/// - Model loading (clip-vit-base-patch32-vision.onnx from %LOCALAPPDATA%\PhotoIQPro\models\)
/// - Image preprocessing: resize to 224×224 (crop if needed), ImageNet normalization
/// - ONNX inference: runs on CPU via Microsoft.ML.OnnxRuntime
/// - Embedding normalization: L2-normalized 512D float vector output
///
/// Initialization is async and runs on the thread pool to avoid blocking the UI (takes 5–30s on first load).
/// </remarks>
public class ClipEngine : IDisposable
{
    private InferenceSession? _session;
    private readonly string _modelsPath;
    private const int Size = 224;
    private static readonly float[] Mean = [0.48145466f, 0.4578275f, 0.40821073f];
    private static readonly float[] Std = [0.26862954f, 0.26130258f, 0.27577711f];

    /// <summary>
    /// Initializes a new instance of the ClipEngine.
    /// </summary>
    /// <remarks>
    /// The ONNX model is not loaded until InitializeAsync is called. This allows dependency injection
    /// without blocking on model I/O during app startup.
    /// </remarks>
    /// <param name="path">Optional custom path to the models directory. If null, defaults to %LOCALAPPDATA%\PhotoIQPro\models.</param>
    public ClipEngine(string? path = null) => _modelsPath = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoIQPro", "models");

    /// <summary>Gets a value indicating whether the ONNX model has been successfully loaded and initialized.</summary>
    public bool IsInitialized => _session != null;

    /// <summary>
    /// Loads and initializes the CLIP vision model from the ONNX file.
    /// </summary>
    /// <remarks>
    /// This method runs on the thread pool to avoid blocking the caller (model loading is CPU-bound and takes 5–30 seconds).
    /// Safe to call multiple times; if already initialized, the old session is disposed and replaced.
    /// Throws FileNotFoundException if the ONNX model file is not found.
    /// </remarks>
    /// <exception cref="FileNotFoundException">Thrown if clip-vit-base-patch32-vision.onnx is not found in the models directory.</exception>
    public async Task InitializeAsync()
    {
        var modelPath = Path.Combine(_modelsPath, "clip-vit-base-patch32-vision.onnx");
        if (!File.Exists(modelPath)) throw new FileNotFoundException($"CLIP model not found at {modelPath}");
        // InferenceSession() is CPU-bound and can take 5–30 s on first load.
        // Run on the thread pool so the caller is never blocked.
        // Create before swapping so that a constructor failure leaves the old session intact.
        var newSession = await Task.Run(() => new InferenceSession(modelPath));
        _session?.Dispose();
        _session = newSession;
    }

    /// <summary>
    /// Computes a 512D L2-normalized image embedding using CLIP vision encoder.
    /// </summary>
    /// <remarks>
    /// The image is resized to 224×224 (center-cropped if necessary), normalized using ImageNet statistics
    /// (mean and standard deviation), and then passed through the CLIP ViT-B/32 model.
    /// The output embedding is L2-normalized to unit length for cosine similarity comparisons.
    ///
    /// Requires InitializeAsync to have been called first. Throws InvalidOperationException if not initialized.
    /// </remarks>
    /// <param name="imagePath">Full path to the image file (any format readable by SixLabors.ImageSharp).</param>
    /// <returns>A 512-element float array representing the normalized image embedding.</returns>
    /// <exception cref="InvalidOperationException">Thrown if InitializeAsync has not been called.</exception>
    public async Task<float[]> GetImageEmbeddingAsync(string imagePath)
    {
        if (_session == null) throw new InvalidOperationException("Not initialized");
        using var img = await Image.LoadAsync<Rgb24>(imagePath);
        img.Mutate(x => x.Resize(new ResizeOptions { Size = new Size(Size, Size), Mode = ResizeMode.Crop }));
        var tensor = BuildTensor(img);
        using var results = _session.Run([NamedOnnxValue.CreateFromTensor("pixel_values", tensor)]);
        var emb = results.First().AsTensor<float>().ToArray();
        var norm = MathF.Sqrt(emb.Sum(v => v * v));
        return emb.Select(v => v / norm).ToArray();
    }

    /// <summary>
    /// Converts a resized Rgb24 image into a [1,3,224,224] NCHW tensor with ImageNet normalisation.
    /// Extracted into a sync method so Span&lt;T&gt; can be used (not allowed in async methods in C# 12).
    /// Uses a pre-allocated backing array to avoid an extra copy from DenseTensor indexer overhead.
    /// </summary>
    private static DenseTensor<float> BuildTensor(Image<Rgb24> img)
    {
        const int planeSize = Size * Size;
        var data = new float[3 * planeSize];

        // Precompute per-channel affine coefficients: normalized = pixel * scale + bias
        float rScale = 1f / (255f * Std[0]), rBias = -Mean[0] / Std[0];
        float gScale = 1f / (255f * Std[1]), gBias = -Mean[1] / Std[1];
        float bScale = 1f / (255f * Std[2]), bBias = -Mean[2] / Std[2];

        if (img.DangerousTryGetSinglePixelMemory(out var pixelMemory))
        {
            // Contiguous buffer — single flat loop, no per-row overhead.
            var pixels = pixelMemory.Span;
            for (int i = 0; i < planeSize; i++)
            {
                var p = pixels[i];
                data[i]               = p.R * rScale + rBias;
                data[planeSize + i]   = p.G * gScale + gBias;
                data[planeSize * 2 + i] = p.B * bScale + bBias;
            }
        }
        else
        {
            // Non-contiguous fallback — access row by row.
            for (int y = 0; y < Size; y++)
            {
                var row = img.DangerousGetPixelRowMemory(y).Span;
                int rowBase = y * Size;
                for (int x = 0; x < Size; x++)
                {
                    var p = row[x];
                    int i = rowBase + x;
                    data[i]               = p.R * rScale + rBias;
                    data[planeSize + i]   = p.G * gScale + gBias;
                    data[planeSize * 2 + i] = p.B * bScale + bBias;
                }
            }
        }

        return new DenseTensor<float>(data, [1, 3, Size, Size]);
    }

    /// <summary>Disposes the ONNX InferenceSession and releases GPU/CPU resources.</summary>
    public void Dispose() => _session?.Dispose();
}
