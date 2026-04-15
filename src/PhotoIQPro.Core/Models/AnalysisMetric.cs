namespace PhotoIQPro.Core.Models;

public class AnalysisMetric
{
    public Guid   Id                { get; set; } = Guid.NewGuid();
    public Guid   PhotoId           { get; set; }
    public string ModelName         { get; set; } = "";
    public int    ImageWidth        { get; set; }
    public int    ImageHeight       { get; set; }
    public string FileFormat        { get; set; } = "";   // jpg, dng, png, etc.
    public long   FileSizeBytes     { get; set; }
    public long   PreprocessingMs   { get; set; }
    public long   InferenceMs       { get; set; }
    public long   TotalAnalysisMs   { get; set; }
    public int    TagCount          { get; set; }
    public int    DescriptionLength { get; set; }
    public bool   GpuUsed           { get; set; }
    public DateTime AnalyzedAt      { get; set; } = DateTime.UtcNow;
}
