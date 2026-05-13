using System;
using PhotoWell.Data;
using PhotoWell.Core.Models;
using Microsoft.EntityFrameworkCore;

var dbPath = @"C:\Users\retli\AppData\Local\PhotoWell\photoiq.db";
var connStr = $"Data Source={dbPath}";
var options = new DbContextOptionsBuilder<PhotoIQContext>()
    .UseSqlite(connStr)
    .Options;

using var db = new PhotoIQContext(options);

try
{
    var totalPhotos = db.MediaFiles.Count();
    var photosComplete = db.MediaFiles.Count(m => m.FaceDetectionStatus == FaceDetectionStatus.Complete);
    var photosNotStarted = db.MediaFiles.Count(m => m.FaceDetectionStatus == FaceDetectionStatus.NotStarted);
    var totalFaces = db.Faces.Count();
    var facesLinked = db.Faces.Count(f => f.PersonId.HasValue);
    var facesUnlinked = db.Faces.Count(f => !f.PersonId.HasValue);
    var photosWithFaces = db.MediaFiles.Count(m => m.Faces.Any());

    Console.WriteLine($@"
=== PhotoIQ Database Status ===
Total photos: {totalPhotos}
  - Face Detection Complete: {photosComplete}
  - Face Detection Not Started: {photosNotStarted}

Total faces detected: {totalFaces}
  - Linked to person: {facesLinked}
  - Unlinked/Unknown: {facesUnlinked}

Photos with at least one face: {photosWithFaces}
");
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
}
