using CommunityToolkit.Mvvm.ComponentModel;
using PhotoWell.Core.Models;

namespace PhotoWell.Desktop.ViewModels;

/// <summary>
/// View model for a single detected face — used in both the "Who is in this photo?"
/// sidebar panel and the bounding-box overlay drawn on the photo.
/// </summary>
public partial class FaceItemViewModel : ObservableObject
{
    public Guid    FaceId        { get; }
    public Guid?   PersonId      { get; set; }
    public string? ThumbnailPath { get; }

    // Normalized coordinates (0–1) relative to the source image.
    // Used by PhotoViewerWindow to draw bounding boxes at the correct position.
    public double X          { get; }
    public double Y          { get; }
    public double FaceWidth  { get; }
    public double FaceHeight { get; }

    /// <summary>Name of the recognised person, or null if unrecognised.</summary>
    [ObservableProperty] private string? _matchedName;

    /// <summary>Editable name — pre-filled with the matched name so users can correct it.</summary>
    [ObservableProperty] private string _nameInput;

    public bool IsNamed => !string.IsNullOrWhiteSpace(MatchedName);

    public FaceItemViewModel(Face face, string? personName)
    {
        FaceId        = face.Id;
        PersonId      = face.PersonId;
        ThumbnailPath = face.ThumbnailPath;
        _matchedName  = personName;
        _nameInput    = personName ?? "";
        X             = face.X;
        Y             = face.Y;
        FaceWidth     = face.Width;
        FaceHeight    = face.Height;
    }

    /// <summary>Updates the person assignment after user-provided name is saved.</summary>
    public void UpdateAssignment(Guid personId, string name)
    {
        PersonId    = personId;
        MatchedName = name;
        NameInput   = name;
    }

    /// <summary>Clears the person assignment after the user unlinks this face.</summary>
    public void ClearAssignment()
    {
        PersonId    = null;
        MatchedName = null;
        NameInput   = "";
    }
}
