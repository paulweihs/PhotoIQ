using System.Text.Json.Serialization;

namespace PhotoIQPro.PromptHarness;

// ── Database entities ──────────────────────────────────────────────────────────

public sealed class SampleImage
{
    public int    Id         { get; set; }
    public string ImagePath  { get; set; } = "";   // original file path
    public string ThumbPath  { get; set; } = "";   // path sent to vision models
    public string FileName   { get; set; } = "";
    public string SampledAt  { get; set; } = "";
}

public sealed class GroundTruth
{
    public int      ImageId        { get; set; }
    public string   Subject        { get; set; } = "";  // e.g. "three children playing"
    public int      PersonCount    { get; set; } = -1;  // -1 = no people / N/A
    public string   Setting        { get; set; } = "";  // e.g. "indoor gym"
    public string[] KeyObjects     { get; set; } = [];  // objects that must be mentioned
    public string[] VerifiedAbsent { get; set; } = [];  // objects confirmed NOT present
    public string   FreeText       { get; set; } = "";  // Claude's full description
    public string   GeneratedAt    { get; set; } = "";
}

public sealed class PromptRecord
{
    public int     Id            { get; set; }
    public string  Version       { get; set; } = "";
    public string  Text          { get; set; } = "";
    public string? ParentVersion { get; set; }
    public string  CreatedAt     { get; set; } = "";
    public string? Notes         { get; set; }
}

public sealed class EvalRun
{
    public string  RunId           { get; set; } = "";
    public string  PromptVersion   { get; set; } = "";
    public string  RunType         { get; set; } = "";  // "quick" | "rich"
    public string  OllamaModel     { get; set; } = "";
    public int     ImagesEvaluated { get; set; }
    public int     ImagesSkipped   { get; set; }
    public int     PassCount       { get; set; }
    public int     FailCount       { get; set; }
    public double  AvgScore        { get; set; }
    public double  AvgMaxScore     { get; set; }
    public string  StartedAt       { get; set; } = "";
    public string? CompletedAt     { get; set; }
}

public sealed class EvalResult
{
    public int     Id                { get; set; }
    public string  RunId             { get; set; } = "";
    public int     ImageId           { get; set; }
    public string  FileName          { get; set; } = "";
    public string? OllamaDescription { get; set; }
    public int     TotalScore        { get; set; }
    public int     MaxScore          { get; set; }
    public bool    Passed            { get; set; }
    public string? Notes             { get; set; }
    // Per-gate scores stored as JSON in DB, deserialized here
    public Dictionary<string, int> GateScores { get; set; } = [];
}

// ── JSON I/O models ────────────────────────────────────────────────────────────

/// <summary>Output of --export-manifest. Consumed by Claude to generate GT.</summary>
public sealed class ManifestFile
{
    [JsonPropertyName("generated_at")] public string          GeneratedAt { get; set; } = "";
    [JsonPropertyName("count")]         public int             Count       { get; set; }
    [JsonPropertyName("images")]        public ManifestEntry[] Images      { get; set; } = [];
}

public sealed class ManifestEntry
{
    [JsonPropertyName("id")]         public int    Id        { get; set; }
    [JsonPropertyName("thumb_path")] public string ThumbPath { get; set; } = "";
    [JsonPropertyName("file_name")]  public string FileName  { get; set; } = "";
}

/// <summary>Input to --import-gt. Claude generates this after reading the manifest.</summary>
public sealed class GroundTruthFile
{
    [JsonPropertyName("generated_at")] public string          GeneratedAt { get; set; } = "";
    [JsonPropertyName("images")]        public GroundTruthEntry[] Images  { get; set; } = [];
}

public sealed class GroundTruthEntry
{
    [JsonPropertyName("id")]               public int      Id             { get; set; }
    [JsonPropertyName("file_name")]        public string   FileName       { get; set; } = "";
    [JsonPropertyName("subject")]          public string   Subject        { get; set; } = "";
    [JsonPropertyName("person_count")]     public int      PersonCount    { get; set; } = -1;
    [JsonPropertyName("setting")]          public string   Setting        { get; set; } = "";
    [JsonPropertyName("key_objects")]      public string[] KeyObjects     { get; set; } = [];
    [JsonPropertyName("verified_absent")]  public string[] VerifiedAbsent { get; set; } = [];
    [JsonPropertyName("free_text")]        public string   FreeText       { get; set; } = "";
}

/// <summary>Output of --rich-eval. Claude reads this, views each image, fills in rich scores.</summary>
public sealed class RichReviewFile
{
    [JsonPropertyName("run_id")]         public string           RunId         { get; set; } = "";
    [JsonPropertyName("prompt_version")] public string           PromptVersion { get; set; } = "";
    [JsonPropertyName("generated_at")]   public string           GeneratedAt   { get; set; } = "";
    [JsonPropertyName("images")]         public RichReviewEntry[] Images        { get; set; } = [];
}

public sealed class RichReviewEntry
{
    [JsonPropertyName("id")]                 public int     Id                { get; set; }
    [JsonPropertyName("thumb_path")]         public string  ThumbPath         { get; set; } = "";
    [JsonPropertyName("file_name")]          public string  FileName          { get; set; } = "";
    [JsonPropertyName("gt_free_text")]       public string  GtFreeText        { get; set; } = "";
    [JsonPropertyName("ollama_description")] public string? OllamaDescription { get; set; }
    [JsonPropertyName("quick_score")]        public int     QuickScore        { get; set; }
    [JsonPropertyName("quick_max")]          public int     QuickMax          { get; set; }
    [JsonPropertyName("quick_passed")]       public bool    QuickPassed       { get; set; }
    // Claude fills these in during rich eval:
    [JsonPropertyName("rich_scores")]        public Dictionary<string,int>? RichScores { get; set; }
    [JsonPropertyName("rich_total")]         public int?    RichTotal         { get; set; }
    [JsonPropertyName("rich_max")]           public int?    RichMax           { get; set; }
    [JsonPropertyName("rich_passed")]        public bool?   RichPassed        { get; set; }
    [JsonPropertyName("rich_notes")]         public string? RichNotes         { get; set; }
}

/// <summary>Input to --import-rich-scores. Claude produces this after annotating RichReviewFile.</summary>
public sealed class RichScoresFile
{
    [JsonPropertyName("run_id")] public string            RunId  { get; set; } = "";
    [JsonPropertyName("scores")] public RichScoreEntry[]  Scores { get; set; } = [];
}

public sealed class RichScoreEntry
{
    [JsonPropertyName("id")]          public int                    Id        { get; set; }
    [JsonPropertyName("rich_scores")] public Dictionary<string,int> RichScores { get; set; } = [];
    [JsonPropertyName("rich_total")]  public int                    RichTotal { get; set; }
    [JsonPropertyName("rich_max")]    public int                    RichMax   { get; set; }
    [JsonPropertyName("rich_passed")] public bool                   RichPassed { get; set; }
    [JsonPropertyName("rich_notes")]  public string?                RichNotes { get; set; }
}

// ── Gate config (from gates.json) ─────────────────────────────────────────────

public sealed class GatesConfig
{
    [JsonPropertyName("version")]               public string       Version              { get; set; } = "";
    [JsonPropertyName("pass_threshold_points")] public int          PassThresholdPoints  { get; set; } = 7;
    [JsonPropertyName("gates")]                 public GateConfig[] Gates                { get; set; } = [];
}

public sealed class GateConfig
{
    [JsonPropertyName("id")]                  public string   Id                { get; set; } = "";
    [JsonPropertyName("name")]                public string   Name              { get; set; } = "";
    [JsonPropertyName("description")]         public string   Description       { get; set; } = "";
    [JsonPropertyName("points")]              public int      Points            { get; set; }
    [JsonPropertyName("type")]                public string   Type              { get; set; } = "";
    // For type=keyword_match:
    [JsonPropertyName("source_field")]        public string?  SourceField       { get; set; }
    [JsonPropertyName("full_credit_pct")]     public double   FullCreditPct     { get; set; } = 0.5;
    [JsonPropertyName("partial_credit_pct")]  public double   PartialCreditPct  { get; set; } = 0.0;
    // For type=verified_absent:
    [JsonPropertyName("penalty_per_hit")]     public int      PenaltyPerHit     { get; set; } = 1;
    // For type=filler_opener:
    [JsonPropertyName("banned_prefixes")]     public string[] BannedPrefixes    { get; set; } = [];
    // For type=word_count:
    [JsonPropertyName("min_words")]           public int      MinWords          { get; set; }
    [JsonPropertyName("max_words")]           public int      MaxWords          { get; set; }
}
