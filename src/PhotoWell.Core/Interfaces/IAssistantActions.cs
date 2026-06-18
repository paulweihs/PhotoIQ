namespace PhotoWell.Core.Interfaces;

/// <summary>
/// Actions the AI chat assistant can perform on the photo library UI.
/// Implemented by MainViewModel; injected into ChatAssistantService.
/// </summary>
public interface IAssistantActions
{
    Task<string> ChatSearchAsync(string query);
    Task<string> ChatFilterByPersonAsync(string name, bool includeUnconfirmed, string? query = null);
    Task<string> ChatFilterByDateAsync(string? startDate, string? endDate);
    Task<string> ChatClearFiltersAsync();
    Task<string> ChatGetLibraryStatsAsync();
    Task<string> ChatOpenPhotoAsync(string filename);
    Task<string> ChatShowPeopleAsync();
    Task<string> ChatExportPhotosAsync(string? personName, bool confirmedOnly);
    Task<string> ChatEmailPhotosAsync(string? personName, bool confirmedOnly, string? filename);
    Task<string> ChatPrintPhotoAsync(string? filename);
    Task<string> ChatSetWallpaperAsync(string? filename);
    Task<string> ChatOpenInEditorAsync(string? editorName, string? filename);
    string GetCurrentContext();
}
