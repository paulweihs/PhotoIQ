using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using PhotoIQPro.Core.Interfaces;

namespace PhotoIQPro.AI.Engines;

/// <summary>
/// Two-stage face pipeline using local ONNX models:
///   Detection  — UltraFace-RFB-640 (ultraface-rfb-640.onnx)
///               Input : [1,3,480,640] normalised to [-1,1]
///               Outputs: "scores"[1,8910,2], "boxes"[1,8910,4] normalised xyxy
///   Embedding  — MobileFaceNet ArcFace (arcface-mobilefacenet.onnx)
///               Input : [1,3,112,112] normalised to [-1,1]
///               Output: "output"[1,128] (unit-normalised internally)
/// Both models run on CPU via ONNX Runtime and degrade silently when missing.
/// </summary>
public sealed class FaceDetectionEngine : IDisposable
{
    private InferenceSession? _detSession;
    private InferenceSession? _embSession;
    private readonly string _modelsPath;

    // Detection constants
    private const int DetW = 640, DetH = 480;
    private const float ConfidenceThreshold = 0.70f;
    private const float IouThreshold = 0.30f;

    // Embedding constants
    private const int EmbSize = 112;

    public bool IsInitialized => _detSession != null && _embSession != null;

    public FaceDetectionEngine(string modelsPath) => _modelsPath = modelsPath;

    public async Task InitializeAsync()
    {
        var detPath = Path.Combine(_modelsPath, "ultraface-rfb-640.onnx");
        var embPath = Path.Combine(_modelsPath, "arcface-mobilefacenet.onnx");

        if (!File.Exists(detPath) || !File.Exists(embPath)) return;

        var det = await Task.Run(() => new InferenceSession(detPath));
        var emb = await Task.Run(() => new InferenceSession(embPath));
        _detSession?.Dispose();
        _embSession?.Dispose();
        _detSession = det;
        _embSession = emb;
    }

    /// <summary>
    /// Detects faces in the image at <paramref name="imagePath"/> and returns
    /// bounding boxes (as fractions of original image size) plus 128-dim embeddings.
    /// Returns empty list if models are unavailable or image fails to load.
    /// </summary>
    public async Task<IReadOnlyList<DetectedFace>> DetectAsync(string imagePath, string facesOutputDir)
    {
        if (!IsInitialized) return [];

        try
        {
            using var img = await Image.LoadAsync<Rgb24>(imagePath);
            int origW = img.Width, origH = img.Height;

            // ── Detection ────────────────────────────────────────────────────
            var detTensor = BuildDetectionTensor(img);
            using var detResults = _detSession!.Run(
            [
                NamedOnnxValue.CreateFromTensor("input", detTensor)
            ]);

            var scores = detResults.First(r => r.Name == "scores").AsTensor<float>();
            var boxes  = detResults.First(r => r.Name == "boxes").AsTensor<float>();

            var candidates = FilterDetections(scores, boxes, origW, origH);
            var kept = NonMaxSuppression(candidates);

            if (kept.Count == 0) return [];

            // ── Crop + Embed ──────────────────────────────────────────────────
            var results = new List<DetectedFace>(kept.Count);
            Directory.CreateDirectory(facesOutputDir);

            foreach (var (x1, y1, x2, y2, conf) in kept)
            {
                // Expand bounding box by 20% to include forehead/chin (clamped to image bounds)
                float cx = (x1 + x2) / 2f, cy = (y1 + y2) / 2f;
                float bw = (x2 - x1) * 1.2f,  bh = (y2 - y1) * 1.2f;
                int cropX = Math.Max(0, (int)(cx - bw / 2));
                int cropY = Math.Max(0, (int)(cy - bh / 2));
                int cropW = Math.Min(origW - cropX, (int)bw);
                int cropH = Math.Min(origH - cropY, (int)bh);

                if (cropW < 20 || cropH < 20) continue;

                // Crop face from original
                using var faceImg = img.Clone(ctx =>
                {
                    ctx.Crop(new Rectangle(cropX, cropY, cropW, cropH));
                    ctx.Resize(EmbSize, EmbSize);
                });

                // Save face crop thumbnail
                var faceId   = Guid.NewGuid();
                var thumbPath = Path.Combine(facesOutputDir, $"{faceId}.jpg");
                await faceImg.SaveAsJpegAsync(thumbPath);

                // Generate embedding
                var embTensor = BuildEmbeddingTensor(faceImg);
                float[] embedding;
                using (var embResults = _embSession!.Run(
                [
                    NamedOnnxValue.CreateFromTensor("input", embTensor)
                ]))
                {
                    embedding = embResults.First().AsTensor<float>().ToArray();
                    Normalize(embedding);
                }

                results.Add(new DetectedFace(
                    X:             (double)cropX / origW,
                    Y:             (double)cropY / origH,
                    Width:         (double)cropW / origW,
                    Height:        (double)cropH / origH,
                    Confidence:    conf,
                    Embedding:     embedding,
                    ThumbnailPath: thumbPath));
            }

            return results;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FaceDetectionEngine] DetectAsync failed for '{imagePath}': {ex.GetType().Name}: {ex.Message}");
            return [];
        }
    }

    // ── Tensor builders ──────────────────────────────────────────────────────

    private static DenseTensor<float> BuildDetectionTensor(Image<Rgb24> src)
    {
        using var resized = src.Clone(ctx => ctx.Resize(DetW, DetH));
        int plane = DetW * DetH;
        var data  = new float[3 * plane];

        resized.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < DetH; y++)
            {
                var row = accessor.GetRowSpan(y);
                int rowBase = y * DetW;
                for (int x = 0; x < DetW; x++)
                {
                    int i = rowBase + x;
                    data[i]           = row[x].R / 127.0f - 1.0f;
                    data[plane + i]   = row[x].G / 127.0f - 1.0f;
                    data[2 * plane + i] = row[x].B / 127.0f - 1.0f;
                }
            }
        });

        return new DenseTensor<float>(data, [1, 3, DetH, DetW]);
    }

    private static DenseTensor<float> BuildEmbeddingTensor(Image<Rgb24> face)
    {
        // face is already 112x112
        int plane = EmbSize * EmbSize;
        var data  = new float[3 * plane];

        face.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < EmbSize; y++)
            {
                var row = accessor.GetRowSpan(y);
                int rowBase = y * EmbSize;
                for (int x = 0; x < EmbSize; x++)
                {
                    int i = rowBase + x;
                    data[i]           = row[x].R / 127.5f - 1.0f;
                    data[plane + i]   = row[x].G / 127.5f - 1.0f;
                    data[2 * plane + i] = row[x].B / 127.5f - 1.0f;
                }
            }
        });

        return new DenseTensor<float>(data, [1, 3, EmbSize, EmbSize]);
    }

    // ── Post-processing ───────────────────────────────────────────────────────

    private static List<(float x1, float y1, float x2, float y2, float conf)> FilterDetections(
        Tensor<float> scores, Tensor<float> boxes, int imgW, int imgH)
    {
        int n = scores.Dimensions[1];
        var result = new List<(float, float, float, float, float)>();

        for (int i = 0; i < n; i++)
        {
            float conf = scores[0, i, 1];
            if (conf < ConfidenceThreshold) continue;

            float x1 = boxes[0, i, 0] * imgW;
            float y1 = boxes[0, i, 1] * imgH;
            float x2 = boxes[0, i, 2] * imgW;
            float y2 = boxes[0, i, 3] * imgH;
            result.Add((x1, y1, x2, y2, conf));
        }

        result.Sort((a, b) => b.Item5.CompareTo(a.Item5)); // descending by confidence
        return result;
    }

    private static List<(float x1, float y1, float x2, float y2, float conf)> NonMaxSuppression(
        List<(float x1, float y1, float x2, float y2, float conf)> boxes)
    {
        var kept = new List<(float, float, float, float, float)>();
        var suppressed = new bool[boxes.Count];

        for (int i = 0; i < boxes.Count; i++)
        {
            if (suppressed[i]) continue;
            kept.Add(boxes[i]);
            for (int j = i + 1; j < boxes.Count; j++)
            {
                if (suppressed[j]) continue;
                if (IoU(boxes[i], boxes[j]) > IouThreshold)
                    suppressed[j] = true;
            }
        }

        return kept;
    }

    private static float IoU(
        (float x1, float y1, float x2, float y2, float _) a,
        (float x1, float y1, float x2, float y2, float _) b)
    {
        float ix1 = Math.Max(a.x1, b.x1), iy1 = Math.Max(a.y1, b.y1);
        float ix2 = Math.Min(a.x2, b.x2), iy2 = Math.Min(a.y2, b.y2);
        float interW = Math.Max(0, ix2 - ix1), interH = Math.Max(0, iy2 - iy1);
        float inter = interW * interH;
        float aArea = (a.x2 - a.x1) * (a.y2 - a.y1);
        float bArea = (b.x2 - b.x1) * (b.y2 - b.y1);
        float union = aArea + bArea - inter;
        return union <= 0 ? 0 : inter / union;
    }

    private static void Normalize(float[] v)
    {
        float norm = MathF.Sqrt(v.Sum(x => x * x));
        if (norm > 0) for (int i = 0; i < v.Length; i++) v[i] /= norm;
    }

    public void Dispose()
    {
        _detSession?.Dispose();
        _embSession?.Dispose();
    }
}
