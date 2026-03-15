# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
# Build
dotnet build PhotoIQPro.sln

# Run (Windows only - WPF app)
dotnet run --project src/PhotoIQPro.Desktop/PhotoIQPro.Desktop.csproj

# Test
dotnet test src/PhotoIQPro.Tests/PhotoIQPro.Tests.csproj

# Single test
dotnet test src/PhotoIQPro.Tests/PhotoIQPro.Tests.csproj --filter "FullyQualifiedName~TestName"
```

Requires: Windows 10/11, .NET 8.0 SDK, Visual Studio 2022 ("`.NET desktop development`" workload for WPF designer).

## Architecture

Six-project solution with strict layering — dependencies flow inward toward Core:

```
PhotoIQPro.Desktop  →  PhotoIQPro.Services  →  PhotoIQPro.Core
PhotoIQPro.Desktop  →  PhotoIQPro.Data      →  PhotoIQPro.Core
PhotoIQPro.Desktop  →  PhotoIQPro.AI        →  PhotoIQPro.Core
PhotoIQPro.Desktop  →  PhotoIQPro.Common
```

**Core** — Domain models (`MediaFile`, `Tag`, `Person`, `Face`, `Collection`, `ExclusionRule`) and interfaces (`IImportService`, `IThumbnailService`, `IMediaFileRepository`, `IDriveService`). Zero external dependencies.

**Common** — Static `AppSettings` with data paths (`%LOCALAPPDATA%\PhotoIQPro\`).

**Data** — EF Core 8 + SQLite. `PhotoIQContext` configures all relationships. `MediaFileRepository` handles CRUD and filtered queries (by path, hash, favorites, unanalyzed). DB at `%LOCALAPPDATA%\PhotoIQPro\photoiq.db`.

**Services** — Three services:
- `ImportService` — hashes files (SHA-256), extracts EXIF via MetadataExtractor, deduplicates, persists
- `ThumbnailService` — generates 150px/400px/800px thumbnails via SixLabors.ImageSharp into `%LOCALAPPDATA%\PhotoIQPro\thumbnails\`
- `DriveService` — scans drives for media files (photos/video/RAW), filters default system exclusions plus user-defined rules

**AI** — `ClipEngine` loads `clip-vit-base-patch32-vision.onnx` via ONNX Runtime, preprocesses images (224×224, ImageNet normalization), returns 768-dim float embeddings. Model stored at `%LOCALAPPDATA%\PhotoIQPro\models\`.

**Desktop** — WPF + MVVM (CommunityToolkit.MVVM). DI wired in `App.xaml.cs`. Two main windows:
- `MainWindow` / `MainViewModel` — photo grid gallery with sidebar and details panel
- `ScanDrivesWindow` / `ScanDrivesViewModel` — drive scanning dialog with exclusion rules, result filtering, and batch import with progress

## Key Entity Relationships

- `MediaFile` ↔ `Tag` (many-to-many)
- `MediaFile` ↔ `Collection` (many-to-many)
- `MediaFile` → `Face` (one-to-many)
- `Face` → `Person` (many-to-one)
- `Collection` → `Collection` (self-referential parent/child)

## Import Pipeline

`DriveService.ScanForMediaAsync()` → `ImportService.ImportFolderAsync()` (hash + EXIF + dedupe) → `ThumbnailService.GenerateThumbnailsAsync()` → `MediaFileRepository.AddAsync()`

## Tech Stack

| Concern | Library |
|---|---|
| UI | WPF (.NET 8, Windows-only) |
| MVVM | CommunityToolkit.MVVM 8.4 |
| ORM | EF Core 8 + SQLite |
| Image processing | SixLabors.ImageSharp 3.1 |
| EXIF metadata | MetadataExtractor 2.9 |
| ML inference | ONNX Runtime 1.24 |
| DI | Microsoft.Extensions.DependencyInjection 8 |
| Tests | xUnit 2.9 |
