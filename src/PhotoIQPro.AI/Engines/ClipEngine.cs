using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhotoIQPro.AI.Engines;

public class ClipEngine : IDisposable
{
    private InferenceSession? _session;
    private readonly string _modelsPath;
    private const int Size = 224;
    private static readonly float[] Mean = [0.48145466f, 0.4578275f, 0.40821073f];
    private static readonly float[] Std = [0.26862954f, 0.26130258f, 0.27577711f];

    public ClipEngine(string? path = null) => _modelsPath = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoIQPro", "models");
    public bool IsInitialized => _session != null;

    public Task InitializeAsync()
    {
        var modelPath = Path.Combine(_modelsPath, "clip-vit-base-patch32-vision.onnx");
        if (!File.Exists(modelPath)) throw new FileNotFoundException($"CLIP model not found at {modelPath}");
        _session = new InferenceSession(modelPath);
        return Task.CompletedTask;
    }

    public Task<float[]> GetImageEmbeddingAsync(string imagePath)
    {
        if (_session == null) throw new InvalidOperationException("Not initialized");
        using var img = Image.Load<Rgb24>(imagePath);
        img.Mutate(x => x.Resize(new ResizeOptions { Size = new Size(Size, Size), Mode = ResizeMode.Crop }));
        var tensor = new DenseTensor<float>([1, 3, Size, Size]);
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                var p = img[x, y];
                tensor[0, 0, y, x] = ((p.R / 255f) - Mean[0]) / Std[0];
                tensor[0, 1, y, x] = ((p.G / 255f) - Mean[1]) / Std[1];
                tensor[0, 2, y, x] = ((p.B / 255f) - Mean[2]) / Std[2];
            }
        using var results = _session.Run([NamedOnnxValue.CreateFromTensor("pixel_values", tensor)]);
        var emb = results.First().AsTensor<float>().ToArray();
        var norm = MathF.Sqrt(emb.Sum(v => v * v));
        return Task.FromResult(emb.Select(v => v / norm).ToArray());
    }

    public void Dispose() => _session?.Dispose();
}
