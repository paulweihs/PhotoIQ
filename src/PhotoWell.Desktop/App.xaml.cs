using System.IO;
using System.Windows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PhotoWell.Common;
using PhotoWell.Core.Interfaces;
using PhotoWell.Core.Models;
using PhotoWell.Data;
using PhotoWell.Data.Repositories;
using PhotoWell.Desktop.ViewModels;
using PhotoWell.Desktop.Views;
using PhotoWell.AI.Engines;
using PhotoWell.Services.Drives;
using PhotoWell.Services.Import;
using PhotoWell.Services.Tagging;
using PhotoWell.Services.Thumbnails;
using PhotoWell.Services.Vision;
using PhotoWell.Services.Models;
using PhotoWell.Services.Search;
using PhotoWell.Desktop.Services;
using PhotoWell.Services.Chat;

namespace PhotoWell.Desktop;

/// <summary>
/// The WPF application entry point and dependency injection container setup.
/// </summary>
/// <remarks>
/// Responsibilities:
/// - Build and configure the dependency injection service container (all repositories, services, ViewModels, Views)
/// - Initialize the SQLite database on first run (schema creation, migrations)
/// - Download CLIP AI models on first run via ClipModelDownloadService
/// - Show a splash screen during initialization
/// - Show the main window immediately; AI setup runs in the background via the status bar
/// - Launch background FTS index rebuild after UI is responsive
/// - Handle unhandled exceptions gracefully
/// - Coordinate clean shutdown (wait for FTS rebuild to finish, dispose OllamaClient)
///
/// The Services property is the global IServiceProvider accessed throughout the application.
/// </remarks>
public partial class App : Application
{
    /// <summary>The global dependency injection service provider, built on application startup.</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    private CancellationTokenSource _ftsCts = new();
    private Task? _ftsTask;

    protected override void OnExit(ExitEventArgs e)
    {
        // Cancel the background FTS rebuild so it stops holding the SQLite write lock.
        // SQLite WAL ensures an incomplete run rolls back cleanly; next startup rebuilds.
        _ftsCts.Cancel();

        // Wait up to 10 s for a clean finish so the index is never left half-written.
        try { _ftsTask?.Wait(TimeSpan.FromSeconds(10)); } catch { }

        StopIpcListener();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();

        // Dispose OllamaClient so HttpClient releases its socket pool cleanly.
        (Services as IDisposable)?.Dispose();

        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        var ex = e.Exception;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Unhandled UI exception: {ex.GetType().Name}: {ex.Message}");
        var inner = ex.InnerException;
        while (inner != null)
        {
            sb.AppendLine($"  Caused by: {inner.GetType().Name}: {inner.Message}");
            inner = inner.InnerException;
        }
        sb.AppendLine(ex.StackTrace);
        AppLog.Error(sb.ToString());
        System.Windows.MessageBox.Show(
            $"An unexpected error occurred:\n\n{e.Exception.Message}\n\nThe error has been logged. Please restart PhotoWell if the app is not responding.",
            "Unexpected Error",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Error);
        e.Handled = true; // prevent crash
    }

    private async void OnStartup(object sender, StartupEventArgs e)
    {
        // async void is required by WPF's event signature. Wrap the entire body so any
        // exception thrown after an await is caught here rather than silently terminating
        // the process via AppDomain.UnhandledException (which bypasses DispatcherUnhandledException).
        try { await StartupCoreAsync(); }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"A fatal error occurred during startup:\n\n{ex.Message}\n\nThe application will close.",
                "Startup Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private async Task StartupCoreAsync()
    {
        if (!CheckSingleInstanceAndHandleArgs()) return;

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        // Prevent shutdown when the splash closes before the main window opens.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Show splash immediately — before any I/O or DI work.
        var splash = new Views.SplashWindow();
        splash.Show();

        MediaFile.CurrentVisionModel        = AppSettings.VisionModelName;
        MediaFile.CurrentPromptVersion      = AppSettings.CurrentPromptVersion;
        MediaFile.CurrentPostProcessVersion = AppSettings.CurrentPostProcessVersion;

        splash.SetStatus("Initializing…");
        await Task.Run(() =>
        {
            AppSettings.EnsureDirectories();
        });

        var prefs = UserPreferences.Load();

        var services = new ServiceCollection();

        // ── Data ─────────────────────────────────────────────────────────────
        services.AddDbContext<PhotoWellContext>(o =>
            o.UseSqlite($"Data Source={AppSettings.DatabasePath};Pooling=False")
             // Sets SQLite's busy_timeout = 5 s on every connection.
             // Default is 0 ms — concurrent writers (FTS rebuild, vision worker, import)
             // would otherwise get SQLITE_BUSY immediately, surfaced as DbUpdateException.
             .AddInterceptors(new PhotoWell.Data.SqliteBusyTimeoutInterceptor(5_000)));
        services.AddScoped<IMediaFileRepository, MediaFileRepository>();
        services.AddScoped<IPersonRepository, PersonRepository>();
        services.AddTransient<IAnalysisMetricRepository, AnalysisMetricRepository>();
        services.AddScoped<ILibraryRepository, LibraryRepository>();

        // ── Services ──────────────────────────────────────────────────────────
        var clipPath = prefs.ClipModelsPath ?? AppSettings.ModelsPath;

        services.AddSingleton<IThumbnailService>(_ =>
            new ThumbnailService(AppSettings.ThumbnailsPath));
        services.AddSingleton<ClipEngine>(_ =>
            new ClipEngine(clipPath));
        services.AddSingleton<ClipTextEngine>(_ =>
            new ClipTextEngine(clipPath));
        services.AddSingleton<ITaggingService>(sp => new ClipTaggingService(
            sp.GetRequiredService<ClipEngine>(),
            sp.GetRequiredService<ClipTextEngine>(),
            clipPath));
        services.AddScoped<IImportService, ImportService>();
        services.AddSingleton<MetricsViewModel>(sp =>
            new MetricsViewModel(sp.GetRequiredService<IServiceScopeFactory>()));
        services.AddSingleton<ISemanticSearchService>(sp =>
            new SemanticSearchService(
                sp.GetRequiredService<ClipTextEngine>(),
                clipPath,
                sp.GetRequiredService<IServiceScopeFactory>()));
        services.AddSingleton<IDriveService, DriveService>();

        services.AddSingleton<OllamaClient>(_ => new OllamaClient(prefs.OllamaBaseUrl));
        services.AddSingleton<IChatModelClient>(sp => sp.GetRequiredService<OllamaClient>());
        services.AddSingleton<IImageUnderstandingService>(sp =>
            new OllamaVisionService(sp.GetRequiredService<OllamaClient>(), AppSettings.VisionModelName));
        services.AddSingleton<IOllamaSetupService>(sp =>
            new OllamaSetupService(prefs.OllamaBaseUrl, sp.GetRequiredService<OllamaClient>()));
        services.AddSingleton<IImagePreprocessor, ImagePreprocessor>();
        services.AddSingleton<FaceDetectionEngine>(_ =>
            new FaceDetectionEngine(AppSettings.ModelsPath));
        services.AddSingleton<IFaceRecognitionService, FaceRecognitionService>();
        services.AddScoped<IFaceService, FaceService>();
        services.AddSingleton<IFaceModelDownloadService, FaceModelDownloadService>();
        services.AddSingleton<IFolderWatcherService, FolderWatcherService>();
        services.AddSingleton<IExifEditService>(_ =>
            new PhotoWell.Services.ExifTool.ExifEditService(AppSettings.ExifToolPath));
        services.AddScoped<IExportService, PhotoWell.Services.Export.ExportService>();

        // ── ViewModels ────────────────────────────────────────────────────────
        services.AddScoped<LibraryViewModel>();
        services.AddTransient<ManageLibrariesWindow>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<SettingsWindow>();
        services.AddTransient<MainViewModel>();
        services.AddTransient<SetupViewModel>(sp => new SetupViewModel(
            sp.GetRequiredService<IOllamaSetupService>(), AppSettings.VisionModelName));
        services.AddTransient<ScanDrivesViewModel>();
        services.AddTransient<FindDuplicatesViewModel>();
        services.AddSingleton<ImportProgressViewModel>();
        services.AddTransient<PhotoViewerViewModel>();
        services.AddTransient<BackupViewModel>();
        services.AddSingleton<IChatAssistantService>(sp =>
            new ChatAssistantService(sp.GetRequiredService<IChatModelClient>()));
        services.AddTransient<ChatViewModel>();

        // ── Views ─────────────────────────────────────────────────────────────
        services.AddTransient<SetupWindow>();
        services.AddTransient<ScanDrivesWindow>();
        services.AddTransient<FindDuplicatesWindow>();
        services.AddTransient<PhotoViewerWindow>();
        services.AddTransient<BackupWindow>();
        services.AddSingleton<OnboardingService>();
        services.AddSingleton<TipToastViewModel>();
        services.AddTransient<FirstRunViewModel>();
        services.AddTransient<FirstRunWindow>();
        services.AddTransient<PeopleViewModel>();
        services.AddTransient<PeopleWindow>();
        services.AddTransient<TagsBrowserViewModel>();
        services.AddTransient<TagsBrowserWindow>();

        services.AddScoped<IExclusionRepository, ExclusionRepository>();
        services.AddSingleton<Core.Interfaces.IClipModelDownloadService, ClipModelDownloadService>();

        splash.SetStatus("Opening database…");
        await Task.Run(() =>
        {
            Services = services.BuildServiceProvider();

            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PhotoWellContext>();
            db.Database.EnsureCreated();

            // WAL mode — allows readers and the background FTS rebuild to run concurrently
            // without blocking each other. Must be set before any long-running write transaction.
            db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");

            // Schema migration — add columns added after initial schema release.
            // ALTER TABLE fails with "duplicate column name" when the column already exists; that
            // specific error is expected and safe to ignore. Any other exception is a real failure
            // (disk full, file locked, corruption) and must NOT be swallowed.
            static void AddColumn(Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade db, string sql)
            {
                try { db.ExecuteSqlRaw(sql); }
                catch (Exception ex) when (ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
                {
                    // Column already exists — expected on all runs after first migration.
                }
                // All other exceptions propagate so startup fails visibly rather than silently.
            }

            AddColumn(db.Database, "ALTER TABLE MediaFiles ADD COLUMN AnalysisStatus INTEGER NOT NULL DEFAULT 0");
            AddColumn(db.Database, "ALTER TABLE MediaFiles ADD COLUMN ClipEmbeddingBytes BLOB NULL");
            // ADR-003 — user-editable description columns
            AddColumn(db.Database, "ALTER TABLE MediaFiles ADD COLUMN UserDescription TEXT NULL");
            AddColumn(db.Database, "ALTER TABLE MediaFiles ADD COLUMN DescriptionEditedAt TEXT NULL");
            AddColumn(db.Database, "ALTER TABLE MediaFiles ADD COLUMN AiModelUsed TEXT NULL");
            // ADR-008 — user caption columns (Caption column already exists from initial schema)
            AddColumn(db.Database, "ALTER TABLE MediaFiles ADD COLUMN UserCaptionRaw TEXT NULL");
            AddColumn(db.Database, "ALTER TABLE MediaFiles ADD COLUMN CaptionEditedAt TEXT NULL");
            // Prompt version tracking — stored alongside AiModelUsed to detect stale descriptions
            AddColumn(db.Database, "ALTER TABLE MediaFiles ADD COLUMN PromptVersion TEXT NULL");
            // Perceptual hash — DCT pHash for near-duplicate detection
            AddColumn(db.Database, "ALTER TABLE MediaFiles ADD COLUMN PerceptualHash INTEGER NULL");
            // Version tracking — records which PhotoWell version imported/analyzed each file
            AddColumn(db.Database, "ALTER TABLE MediaFiles ADD COLUMN ImportedWithVersion TEXT NULL");
            AddColumn(db.Database, "ALTER TABLE MediaFiles ADD COLUMN AnalyzedWithVersion TEXT NULL");
            // Post-processing pipeline version — allows re-analysis when TextNormalizer logic changes
            AddColumn(db.Database, "ALTER TABLE MediaFiles ADD COLUMN PostProcessVersion TEXT NULL");

            // Data migration — mark already-analyzed photos as Complete so they are not re-queued.
            db.Database.ExecuteSqlRaw("UPDATE MediaFiles SET AnalysisStatus = 2 WHERE IsAnalyzed = 1 AND AnalysisStatus = 0");

            // Crash recovery — any photo still marked Processing was interrupted; reset to Pending.
            db.Database.ExecuteSqlRaw("UPDATE MediaFiles SET AnalysisStatus = 0 WHERE AnalysisStatus = 1");

            // Model rename migration — 'minicpm-v' was the direct Ollama model name; we now use
            // 'photowell-minicpm-v', a derived model with num_ctx=1024 baked in. Photos analyzed
            // with the old name are identical in quality — just retag them so they don't show as
            // outdated and don't get unnecessarily re-queued.
            db.Database.ExecuteSqlRaw(
                "UPDATE MediaFiles SET AiModelUsed = 'photowell-minicpm-v' WHERE AiModelUsed = 'minicpm-v'");

            // Failed-vision recovery — photos where vision ran against the missing/broken
            // 'photowell-minicpm-v' model and returned empty. Reset them to VisionPending (4)
            // so the background worker picks them up and re-analyzes them correctly.
            // Only resets Complete photos (AnalysisStatus=2) with an empty description and no
            // user description, so user edits and genuinely-blank images are not disturbed.
            db.Database.ExecuteSqlRaw(
                "UPDATE MediaFiles SET AnalysisStatus = 4 " +
                "WHERE AnalysisStatus = 2 AND AiDescription = '' AND UserDescription IS NULL " +
                "AND AiModelUsed = 'photowell-minicpm-v'");

            // Double-comma cleanup — TextNormalizer gendered-noun substitutions could produce ",,"
            // when the original text already had a comma after the substituted word. Fixed in the
            // normalizer pipeline, but existing stored descriptions need a one-time in-place fix.
            db.Database.ExecuteSqlRaw(
                "UPDATE MediaFiles SET AiDescription = REPLACE(AiDescription, ',,', ',') " +
                "WHERE AiDescription LIKE '%,,%'");

            AddColumn(db.Database, "ALTER TABLE MediaFiles ADD COLUMN IsExcluded INTEGER NOT NULL DEFAULT 0");

            // Video exclusion — video playback is not yet supported; hide any video files that were
            // imported before AllExtensions was restricted to photo+RAW only.
            db.Database.ExecuteSqlRaw(
                "UPDATE MediaFiles SET IsExcluded = 1 WHERE IsExcluded = 0 AND LOWER(Extension) IN " +
                "('.mp4', '.mov', '.avi', '.mkv', '.webm', '.flv', '.wmv', '.m4v')");

            // ── Analysis metrics table ────────────────────────────────────────────
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS AnalysisMetrics (
                    Id                TEXT PRIMARY KEY,
                    PhotoId           TEXT NOT NULL,
                    ModelName         TEXT NOT NULL DEFAULT '',
                    ImageWidth        INTEGER NOT NULL DEFAULT 0,
                    ImageHeight       INTEGER NOT NULL DEFAULT 0,
                    FileFormat        TEXT NOT NULL DEFAULT '',
                    FileSizeBytes     INTEGER NOT NULL DEFAULT 0,
                    PreprocessingMs   INTEGER NOT NULL DEFAULT 0,
                    InferenceMs       INTEGER NOT NULL DEFAULT 0,
                    TotalAnalysisMs   INTEGER NOT NULL DEFAULT 0,
                    TagCount          INTEGER NOT NULL DEFAULT 0,
                    DescriptionLength INTEGER NOT NULL DEFAULT 0,
                    GpuUsed           INTEGER NOT NULL DEFAULT 0,
                    AnalyzedAt        TEXT NOT NULL
                )
                """);

            // ── Library / Album tables (ADR-002) ─────────────────────────────────
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS Libraries (
                    Id          TEXT PRIMARY KEY,
                    Name        TEXT NOT NULL,
                    Description TEXT NULL,
                    CreatedAt   TEXT NOT NULL DEFAULT (datetime('now'))
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS Albums (
                    Id           TEXT PRIMARY KEY,
                    LibraryId    TEXT NOT NULL REFERENCES Libraries(Id) ON DELETE CASCADE,
                    Name         TEXT NOT NULL,
                    Description  TEXT NULL,
                    CoverPhotoId TEXT NULL REFERENCES MediaFiles(Id) ON DELETE SET NULL,
                    CreatedAt    TEXT NOT NULL DEFAULT (datetime('now'))
                )
                """);

            // Junction table — column names must match EF Core's UsingEntity convention.
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS photo_albums (
                    AlbumsId     TEXT NOT NULL REFERENCES Albums(Id) ON DELETE CASCADE,
                    MediaFilesId TEXT NOT NULL REFERENCES MediaFiles(Id) ON DELETE CASCADE,
                    PRIMARY KEY  (AlbumsId, MediaFilesId)
                )
                """);

            // ── ExclusionRules table (may not exist in DBs created before this was added) ─
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS ExclusionRules (
                    Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                    Value     TEXT    NOT NULL,
                    IsFullPath INTEGER NOT NULL DEFAULT 0
                )
                """);

            // ── Face recognition tables (Phase 1/2) ──────────────────────────────────
            // Created explicitly so existing installs (where EnsureCreated already ran)
            // get the tables without requiring a full DB reset.
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS People (
                    Id               TEXT    PRIMARY KEY,
                    Name             TEXT    NULL,
                    NormalizedName   TEXT    NULL,
                    IsNamed          INTEGER NOT NULL DEFAULT 0,
                    IsVisible        INTEGER NOT NULL DEFAULT 1,
                    IsFavorite       INTEGER NOT NULL DEFAULT 0,
                    KeyFaceId        TEXT    NULL,
                    AverageEmbedding BLOB    NULL,
                    FaceCount        INTEGER NOT NULL DEFAULT 0,
                    DateCreated      TEXT    NOT NULL DEFAULT (datetime('now')),
                    DateModified     TEXT    NOT NULL DEFAULT (datetime('now'))
                )
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS Faces (
                    Id                      TEXT    PRIMARY KEY,
                    MediaFileId             TEXT    NOT NULL REFERENCES MediaFiles(Id) ON DELETE CASCADE,
                    PersonId                TEXT    NULL     REFERENCES People(Id) ON DELETE SET NULL,
                    X                       REAL    NOT NULL DEFAULT 0,
                    Y                       REAL    NOT NULL DEFAULT 0,
                    Width                   REAL    NOT NULL DEFAULT 0,
                    Height                  REAL    NOT NULL DEFAULT 0,
                    Embedding               BLOB    NULL,
                    DetectionConfidence     REAL    NOT NULL DEFAULT 0,
                    IdentificationConfidence REAL   NULL,
                    ThumbnailPath           TEXT    NULL,
                    DateDetected            TEXT    NOT NULL DEFAULT (datetime('now'))
                )
                """);

            // FaceCount added to People after initial schema — safe to ignore duplicate
            AddColumn(db.Database, "ALTER TABLE People ADD COLUMN FaceCount INTEGER NOT NULL DEFAULT 0");

            // Face detection columns added to MediaFiles
            AddColumn(db.Database, "ALTER TABLE MediaFiles ADD COLUMN FaceDetectionStatus INTEGER NOT NULL DEFAULT 0");
            AddColumn(db.Database, "ALTER TABLE MediaFiles ADD COLUMN FacesReviewed INTEGER NOT NULL DEFAULT 0");
            // User-applied rotation override (on top of EXIF) for display orientation control
            AddColumn(db.Database, "ALTER TABLE MediaFiles ADD COLUMN UserRotation INTEGER NOT NULL DEFAULT 0");

            // User confirmation flag — preserves manual face-to-person links across re-analysis
            AddColumn(db.Database, "ALTER TABLE Faces ADD COLUMN IsUserConfirmed INTEGER NOT NULL DEFAULT 0");
            // Prompt suppression — skip review dialog for this person when user opts out
            AddColumn(db.Database, "ALTER TABLE People ADD COLUMN SuppressPrompt INTEGER NOT NULL DEFAULT 0");
            // Manual person mention — user-tagged person with no detected face (survives re-detection)
            AddColumn(db.Database, "ALTER TABLE Faces ADD COLUMN IsManualMention INTEGER NOT NULL DEFAULT 0");

            // ── Performance indexes (idempotent — safe to run on existing DBs) ──────
            db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_MediaFiles_AnalysisStatus ON MediaFiles (AnalysisStatus)");
            db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_MediaFiles_DateTaken ON MediaFiles (DateTaken)");
            db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_MediaFiles_IsFavorite ON MediaFiles (IsFavorite)");
            db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_MediaFiles_Lat_Lon ON MediaFiles (Latitude, Longitude)");
            // Indexes identified by pre-release DBA review — commonly filtered columns missing coverage.
            db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_MediaFiles_IsExcluded ON MediaFiles (IsExcluded)");
            db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_MediaFiles_Extension ON MediaFiles (Extension)");
            db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_MediaFiles_FaceDetectionStatus ON MediaFiles (FaceDetectionStatus, DateImported)");
            db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_MediaFiles_IsExcluded_AnalysisStatus_DateTaken ON MediaFiles (IsExcluded, AnalysisStatus, DateTaken)");
            db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_Faces_PersonId ON Faces (PersonId)");
            db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_Faces_IsUserConfirmed ON Faces (IsUserConfirmed)");

            // ── FTS5 search index ─────────────────────────────────────────────────
            db.Database.ExecuteSqlRaw("""
                CREATE VIRTUAL TABLE IF NOT EXISTS MediaFilesSearch USING fts5(
                    media_file_id UNINDEXED,
                    description,
                    tags,
                    filename,
                    camera,
                    date_text,
                    folder
                )
                """);
        });

        // ── One-time face detection re-queue (normalization fix) ─────────────
        // Prior builds used the wrong preprocessing formula for UltraFace-RFB-640
        // (pixel/127.0-1.0 instead of (pixel-127.5)/128.0), causing all confidence
        // scores to fall below threshold. Reset photos whose Complete status has no
        // corresponding Face rows so the background worker re-processes them.
        if (!prefs.FaceDetectionNormalizationFixed)
        {
            using var fixScope = Services.CreateScope();
            var fixDb = fixScope.ServiceProvider.GetRequiredService<PhotoWellContext>();
            fixDb.Database.ExecuteSqlRaw("""
                UPDATE MediaFiles
                SET    FaceDetectionStatus = 0
                WHERE  FaceDetectionStatus = 1
                AND    NOT EXISTS (
                    SELECT 1 FROM Faces WHERE Faces.MediaFileId = MediaFiles.Id
                )
                """);
            prefs.FaceDetectionNormalizationFixed = true;
            prefs.Save();
        }

        // ── One-time orphaned face thumbnail cleanup ──────────────────────────
        // Face thumbnail .jpg files accumulate on disk without a matching Face DB row when:
        //   (a) the embedding step crashed and DetectAsync returned empty (no BatchAddFacesAsync)
        //   (b) a MediaFile was removed from the library (cascade deletes the Face row but not the file)
        // Going forward both paths now delete the files. This sweep cleans up the historical backlog.
        if (!prefs.OrphanedFaceThumbnailsCleanedUp)
        {
            try
            {
                var facesDir = AppSettings.FacesPath;
                if (Directory.Exists(facesDir))
                {
                    using var cleanScope = Services.CreateScope();
                    var cleanDb = cleanScope.ServiceProvider.GetRequiredService<PhotoWellContext>();
                    var referenced = await cleanDb.Faces
                        .Where(f => f.ThumbnailPath != null)
                        .Select(f => f.ThumbnailPath!)
                        .ToListAsync();
                    var referencedSet = new HashSet<string>(referenced, StringComparer.OrdinalIgnoreCase);
                    var orphans = Directory.EnumerateFiles(facesDir, "*.jpg")
                        .Where(f => !referencedSet.Contains(f))
                        .ToList();
                    foreach (var f in orphans)
                        try { File.Delete(f); } catch { }
                    AppLog.Info($"App: Cleaned up {orphans.Count} orphaned face thumbnail(s).");
                }
            }
            catch (Exception ex)
            {
                AppLog.Error($"App: Orphaned face thumbnail cleanup failed: {ex.GetType().Name}: {ex.Message}");
            }
            prefs.OrphanedFaceThumbnailsCleanedUp = true;
            prefs.Save();
        }

        // ── CLIP model download (first run / missing files) ────────────────────
        // Downloads any absent model files before attempting to initialise the
        // ONNX sessions.  Progress is shown on the splash with a green bar.
        splash.SetStatus("Checking AI models…");
        var modelDownloader = new ClipModelDownloadService();
        if (!modelDownloader.AreModelsInstalled())
        {
            try
            {
                await foreach (var progress in modelDownloader.EnsureModelsAsync())
                {
                    switch (progress.Stage)
                    {
                        case Core.Interfaces.ClipDownloadStage.Downloading:
                            splash.SetDownloadProgress(
                                progress.Fraction >= 0 ? progress.Fraction : 0,
                                progress.Message);
                            break;
                        case Core.Interfaces.ClipDownloadStage.Error:
                            splash.ResetToSpinner();
                            splash.SetStatus(progress.Message.Split('\n')[0]); // first line only
                            await Task.Delay(3000); // let user read the message
                            break;
                        case Core.Interfaces.ClipDownloadStage.Done:
                            splash.ResetToSpinner();
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Error($"Model download failed unexpectedly: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                splash.ResetToSpinner();
            }
        }

        // Download face detection models if missing (Pro feature only)
        if (!prefs.IsExpressMode && prefs.EnableFaceDetection)
        {
            var faceModelDownloader = new FaceModelDownloadService();
            if (!faceModelDownloader.AreModelsInstalled())
            {
                try
                {
                    splash.SetStatus("Checking face detection models…");
                    await foreach (var progress in faceModelDownloader.EnsureModelsAsync())
                    {
                        var faceProgress = progress as FaceDownloadProgress;
                        if (faceProgress != null)
                        {
                            switch (faceProgress.Stage)
                            {
                                case FaceDownloadStage.Checking:
                                case FaceDownloadStage.Downloading:
                                    splash.SetStatus(faceProgress.Message);
                                    break;
                                case FaceDownloadStage.Error:
                                    splash.SetStatus(faceProgress.Message.Split('\n')[0]);
                                    await Task.Delay(2000);
                                    break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Error($"Face model download failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        // Pre-warm CLIP models while the splash is still visible.
        // InferenceSession() can take 5–30 s; doing it here prevents the main window
        // from showing a spinning cursor after first paint.
        splash.SetStatus("Loading AI models…");
        try
        {
            if (Services.GetRequiredService<ITaggingService>() is ClipTaggingService cts)
                await cts.InitializeAsync();
        }
        catch { /* model files may not be installed — degraded mode, not a startup failure */ }

        // Initialize face detection engine if models are available
        try
        {
            AppLog.Info("App: Starting FaceDetectionEngine initialization...");
            await Services.GetRequiredService<FaceDetectionEngine>().InitializeAsync();
            AppLog.Info("App: FaceDetectionEngine initialization complete.");
        }
        catch (Exception ex)
        {
            AppLog.Error($"App: FaceDetectionEngine initialization failed: {ex.GetType().Name}: {ex.Message}");
        }

        // FTS rebuild — runs in the background while the UI loads.
        // OnExit waits up to 10 s for this task so the index is never left half-written.
        //
        // SKIPPED when there is a pending import queue: the rebuild holds SQLite's write
        // lock for the full duration of a large DELETE + INSERT, and the queue resume starts
        // 2 s after the main window opens. Running both concurrently causes SQLITE_BUSY
        // (database is locked). The FTS index is updated incrementally by UpsertFtsAsync
        // on every imported file, so no rebuild is needed after a queue-resume import.
        bool hasPendingQueue = File.Exists(AppSettings.ImportQueuePath);
        if (!hasPendingQueue)
        {
            var ftsCt = _ftsCts.Token;
            _ftsTask = Task.Run(async () =>
            {
                try
                {
                    using var scope = Services.CreateScope();
                    var repo = scope.ServiceProvider.GetRequiredService<IMediaFileRepository>();
                    await repo.RebuildFtsIndexAsync(ftsCt);
                }
                catch (OperationCanceledException) { /* clean exit */ }
                catch (Exception ex)
                {
                    AppLog.Error($"FTS index rebuild failed: {ex.Message}");
                }
            }, ftsCt);
        }

        splash.Close();

        // ── Main window ────────────────────────────────────────────────────────
        // Show the main window immediately so the user can start importing/browsing.
        // AI setup (Ollama install + model pull) runs in the background and shows
        // progress in the status bar — the user is never blocked waiting for it.
        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        mainWindow.Show();

        var mainVm = (MainViewModel)mainWindow.DataContext;

        // Wire chat assistant to MainViewModel so it can execute library actions
        var chatService = Services.GetRequiredService<IChatAssistantService>();
        chatService.SetActions(mainVm);

        // ── Onboarding / tip toast ─────────────────────────────────────────────
        var onboarding = Services.GetRequiredService<OnboardingService>();
        var tipToastVm = Services.GetRequiredService<TipToastViewModel>();

        if (onboarding.ShouldShowOnboarding())
        {
            var firstRunWindow = Services.GetRequiredService<FirstRunWindow>();
            firstRunWindow.ShowDialog();

            var firstRunVm = (ViewModels.FirstRunViewModel)firstRunWindow.DataContext;
            if (firstRunVm.UserWantsToAddFolder)
                mainVm.ScanDrivesCommand.Execute(null);
        }
        else if (onboarding.ShouldShowTip())
        {
            var tipTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(800)
            };
            tipTimer.Tick += (_, _) =>
            {
                tipTimer.Stop();
                tipToastVm.Show();
            };
            tipTimer.Start();
        }

        // Start the resume-import countdown now — after onboarding so the dialog never
        // races with FirstRunWindow.ShowDialog() blocking the UI thread.
        mainVm.ScheduleResumeImportQueue();

        // Handle CLI args passed directly to this instance (e.g. context menu launch).
        var (cmd, path) = ParseCliArgs();
        if (cmd is not null && path is not null)
            HandleIpcCommand(cmd, path);

        // ── Background AI setup ────────────────────────────────────────────────
        // Kick off Ollama/model verification on the same MainViewModel instance that
        // MainWindow is using (retrieved from the window's DataContext after Show()).
        var setupService = Services.GetRequiredService<IOllamaSetupService>();
        var modelName    = AppSettings.VisionModelName;

        void StartSetup() => mainVm.StartAiSetup(setupService, modelName);
        mainVm.SetAiSetupRetryAction(StartSetup);
        StartSetup();
    }
}
