using System.IO;
using System.Windows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PhotoIQPro.Common;
using PhotoIQPro.Core.Interfaces;
using PhotoIQPro.Data;
using PhotoIQPro.Data.Repositories;
using PhotoIQPro.Desktop.ViewModels;
using PhotoIQPro.Desktop.Views;
using PhotoIQPro.AI.Engines;
using PhotoIQPro.Services.Drives;
using PhotoIQPro.Services.Import;
using PhotoIQPro.Services.Tagging;
using PhotoIQPro.Services.Thumbnails;
using PhotoIQPro.Services.Vision;
using PhotoIQPro.Services.Models;
using PhotoIQPro.Services.Search;

namespace PhotoIQPro.Desktop;

/// <summary>
/// The WPF application entry point and dependency injection container setup.
/// </summary>
/// <remarks>
/// Responsibilities:
/// - Build and configure the dependency injection service container (all repositories, services, ViewModels, Views)
/// - Initialize the SQLite database on first run (schema creation, migrations)
/// - Download CLIP AI models on first run via ClipModelDownloadService
/// - Show a splash screen during initialization
/// - Run the SetupWindow to check Ollama availability before showing the main window
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

    /// <summary>Tracks the background FTS index rebuild task so OnExit can wait for it to complete safely.</summary>
    private Task _ftsRebuildTask = Task.CompletedTask;

    protected override void OnExit(ExitEventArgs e)
    {
        // Wait for FTS rebuild — up to 10 s; avoids a half-written index on fast exit.
        try { _ftsRebuildTask.Wait(TimeSpan.FromSeconds(10)); }
        catch { /* timeout or exception — acceptable on shutdown */ }

        // Dispose OllamaClient so HttpClient releases its socket pool cleanly.
        (Services as IDisposable)?.Dispose();

        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error($"Unhandled UI exception: {e.Exception.GetType().Name}: {e.Exception.Message}\n{e.Exception.StackTrace}");
        System.Windows.MessageBox.Show(
            $"An unexpected error occurred:\n\n{e.Exception.Message}\n\nThe error has been logged. Please restart PhotoIQ Pro if the app is not responding.",
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
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        // Prevent shutdown when the splash closes before the main window opens.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Show splash immediately — before any I/O or DI work.
        var splash = new Views.SplashWindow();
        splash.Show();

        splash.SetStatus("Initializing…");
        await Task.Run(() =>
        {
            AppSettings.EnsureDirectories();
        });

        var prefs = UserPreferences.Load();

        var services = new ServiceCollection();

        // ── Data ─────────────────────────────────────────────────────────────
        services.AddDbContext<PhotoIQContext>(o =>
            o.UseSqlite($"Data Source={AppSettings.DatabasePath};Pooling=False")
             // Sets SQLite's busy_timeout = 5 s on every connection.
             // Default is 0 ms — concurrent writers (FTS rebuild, vision worker, import)
             // would otherwise get SQLITE_BUSY immediately, surfaced as DbUpdateException.
             .AddInterceptors(new PhotoIQPro.Data.SqliteBusyTimeoutInterceptor(5_000)));
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
        services.AddSingleton<IImageUnderstandingService>(sp =>
            new OllamaVisionService(sp.GetRequiredService<OllamaClient>(), prefs.VisionModelName));
        services.AddSingleton<IOllamaSetupService>(_ => new OllamaSetupService(prefs.OllamaBaseUrl));
        services.AddSingleton<IImagePreprocessor, ImagePreprocessor>();
        services.AddSingleton<FaceDetectionEngine>(_ =>
            new FaceDetectionEngine(AppSettings.ModelsPath));
        services.AddSingleton<IFaceRecognitionService, FaceRecognitionService>();
        services.AddScoped<IFaceService, FaceService>();
        services.AddSingleton<IFolderWatcherService, FolderWatcherService>();

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

        // ── Views ─────────────────────────────────────────────────────────────
        services.AddTransient<SetupWindow>();
        services.AddTransient<ScanDrivesWindow>();
        services.AddTransient<FindDuplicatesWindow>();
        services.AddTransient<PhotoViewerWindow>();
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
            var db = scope.ServiceProvider.GetRequiredService<PhotoIQContext>();
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

            // Data migration — mark already-analyzed photos as Complete so they are not re-queued.
            db.Database.ExecuteSqlRaw("UPDATE MediaFiles SET AnalysisStatus = 2 WHERE IsAnalyzed = 1 AND AnalysisStatus = 0");

            // Crash recovery — any photo still marked Processing was interrupted; reset to Pending.
            db.Database.ExecuteSqlRaw("UPDATE MediaFiles SET AnalysisStatus = 0 WHERE AnalysisStatus = 1");
            AddColumn(db.Database, "ALTER TABLE MediaFiles ADD COLUMN IsExcluded INTEGER NOT NULL DEFAULT 0");

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

            // ── Performance indexes (idempotent — safe to run on existing DBs) ──────
            db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_MediaFiles_AnalysisStatus ON MediaFiles (AnalysisStatus)");
            db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_MediaFiles_DateTaken ON MediaFiles (DateTaken)");
            db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_MediaFiles_IsFavorite ON MediaFiles (IsFavorite)");
            db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_MediaFiles_Lat_Lon ON MediaFiles (Latitude, Longitude)");

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

        // FTS rebuild — runs in the background while the UI loads.
        // OnExit waits up to 10 s for this task so the index is never left half-written.
        //
        // SKIPPED when there is a pending import queue: the rebuild holds SQLite's write
        // lock for the full duration of a large DELETE + INSERT, and the queue resume starts
        // 2 s after the main window opens. Running both concurrently causes SQLITE_BUSY
        // (database is locked). The FTS index is updated incrementally by UpsertFtsAsync
        // on every imported file, so no rebuild is needed after a queue-resume import.
        bool hasPendingQueue = File.Exists(AppSettings.ImportQueuePath);
        _ftsRebuildTask = hasPendingQueue ? Task.CompletedTask : Task.Run(async () =>
        {
            try
            {
                using var scope = Services.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<IMediaFileRepository>();
                await repo.RebuildFtsIndexAsync();
            }
            catch (Exception ex)
            {
                AppLog.Error($"FTS index rebuild failed: {ex.Message}");
            }
        });

        splash.Close();

        // ── Startup window gating ──────────────────────────────────────────────
        // SetupWindow checks Ollama health + model presence. If already ready it
        // auto-dismisses in ~600 ms. If not, it guides the user through model download.
        // MainWindow is shown only after setup completes (or the user clicks Continue Anyway).
        var setupWindow = Services.GetRequiredService<SetupWindow>();
        setupWindow.SetupCompleted += () =>
        {
            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            mainWindow.Show();
        };
        setupWindow.Show();
    }
}
