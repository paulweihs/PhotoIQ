namespace PhotoWell.Desktop.ViewModels;

/// <summary>Tag data displayed in detail panels. Includes Id for remove operations without DB lookup.</summary>
public record TagViewModel(Guid Id, string Name, bool IsAIGenerated);
