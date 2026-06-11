using System.Text.Json;

namespace PhotoWell.Common;

public record ExternalEditorEntry(string Name, string ExePath);

/// <summary>
/// User settings loaded from and persisted to <c>settings.json</c>.
/// Properties are plain auto-properties — changes are NOT automatically written to disk.
/// Call <see cref="Save"/> explicitly after modifying any property, or changes will be
/// lost when the process exits.
/// </summary>
public class UserPreferences
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PhotoWell", "settings.json");

    // ExecutionAndPublication ensures only one thread runs Load(); all others block until done.
    // The returned instance is a plain mutable object — property assignments after first load
    // are NOT thread-safe. Callers that write properties must do so from a single thread (UI),
    // then call Save() to persist. Concurrent reads from background threads are safe because
    // C# property reads are atomic for reference and bool/int types on aligned memory.
    private static readonly Lazy<UserPreferences> _lazy =
        new(Load, LazyThreadSafetyMode.ExecutionAndPublication);
    public static UserPreferences Current => _lazy.Value;

    public string OllamaBaseUrl         { get; set; } = "http://127.0.0.1:11434";
    public string VisionModelName       { get; set; } = "llama3.2-vision";
    public string? ClipModelsPath       { get; set; }
    public bool   IsExpressMode         { get; set; }
    public int    MaxParallelThreads    { get; set; } = 2;
    public bool   HighQualityThumbnails { get; set; }

    /// <summary>Enable face detection (Pro feature). Disabled in Express mode. Default true for Standard tier.</summary>
    public bool   EnableFaceDetection   { get; set; } = true;

    /// <summary>Maximum image dimension (px) sent to the vision model. 512=Fast, 768=Balanced, 1024=Quality. Default 512.</summary>
    public int    VisionImageSize       { get; set; } = 512;

    /// <summary>Numeric value for the related-images time window (paired with RelatedTimeUnitIndex). Default 2.</summary>
    public int  RelatedTimeValue        { get; set; } = 2;
    /// <summary>Unit index for the time window: 0=Minutes, 1=Hours, 2=Days, 3=Weeks, 4=Months. Default 1 (Hours).</summary>
    public int  RelatedTimeUnitIndex    { get; set; } = 1;
    /// <summary>Index into the distance step array for related photos. 0=100m … 5=50km. Default 2 (1 km).</summary>
    public int  RelatedDistanceStepIndex { get; set; } = 2;
    /// <summary>When true distance labels show feet/miles; when false metres/kilometres.</summary>
    public bool UseImperialUnits        { get; set; } = false;

    /// <summary>True once the first-run onboarding flow has been completed or skipped.</summary>
    public bool HasCompletedOnboarding { get; set; } = false;

    /// <summary>Show a startup tip toast on each launch. Set false to suppress permanently.</summary>
    public bool TipsEnabled { get; set; } = true;

    /// <summary>Index into the tips array for the next tip to display; cycles through all tips.</summary>
    public int LastTipIndex { get; set; } = 0;

    /// <summary>Set to true after a one-time reset that re-queues photos processed under the buggy detection normalization.</summary>
    public bool FaceDetectionNormalizationFixed { get; set; } = false;

    /// <summary>Set to true after a one-time sweep that deletes face thumbnail files not referenced by any Face DB row.</summary>
    public bool OrphanedFaceThumbnailsCleanedUp { get; set; } = false;

    /// <summary>
    /// Ollama model used for the AI chat assistant. Needs reliable tool-calling support.
    /// Default llama3.1:8b gives the best accuracy; switch to llama3.2:3b for faster responses
    /// on lower-end hardware.
    /// </summary>
    public string ChatModelName { get; set; } = "llama3.1:8b";

    /// <summary>User-configured external editors shown in the Send To submenu.</summary>
    public List<ExternalEditorEntry> ExternalEditors { get; set; } = [];

    /// <summary>
    /// Gallery sort history, most-recent sort first.
    /// Each entry is "FieldName:asc" or "FieldName:desc".
    /// Valid field names: DateTaken, FileName, FileSize, DateImported, Camera.
    /// </summary>
    public List<string> GallerySortHistory { get; set; } = ["DateTaken:desc"];

    public static UserPreferences Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<UserPreferences>(json) ?? new();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"Failed to load settings from '{SettingsPath}': {ex.GetType().Name}: {ex.Message}");
        }
        return new UserPreferences();
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Failed to save settings to '{SettingsPath}': {ex.GetType().Name}: {ex.Message}");
        }
    }
}
