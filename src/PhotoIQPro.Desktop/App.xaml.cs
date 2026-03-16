// File: PhotoIQPro.Desktop/App.xaml.cs  (UPDATED — replace entire file)
//
// Changes:
//   1. Added IDriveService → DriveService registration
//   2. Added ScanDrivesViewModel as Transient
//   3. Added ScanDrivesWindow as Transient

using System;
using System.Windows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PhotoIQPro.Common;
using PhotoIQPro.Core.Interfaces;
using PhotoIQPro.Data;
using PhotoIQPro.Data.Repositories;
using PhotoIQPro.Desktop.ViewModels;
using PhotoIQPro.Desktop.Views;
using PhotoIQPro.AI;
using PhotoIQPro.AI.Engines;
using PhotoIQPro.Services.Drives;      //  ← NEW
using PhotoIQPro.Services.Import;
using PhotoIQPro.Services.Tagging;
using PhotoIQPro.Services.Thumbnails;
using PhotoIQPro.Services.Vision;

namespace PhotoIQPro.Desktop;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppSettings.EnsureDirectories();

        var services = new ServiceCollection();

        // ── Data ────────────────────────────────────────────────────
        services.AddDbContext<PhotoIQContext>(o =>
            o.UseSqlite($"Data Source={AppSettings.DatabasePath}"));
        services.AddScoped<IMediaFileRepository, MediaFileRepository>();

        // ── Services ────────────────────────────────────────────────
        services.AddSingleton<IThumbnailService>(_ =>
            new ThumbnailService(AppSettings.ThumbnailsPath));
        services.AddSingleton<ClipEngine>(_ => new ClipEngine(AppSettings.ModelsPath));
        services.AddSingleton<ClipTextEngine>(_ => new ClipTextEngine(AppSettings.ModelsPath));
        services.AddSingleton<ITaggingService>(sp => new ClipTaggingService(
            sp.GetRequiredService<ClipEngine>(),
            sp.GetRequiredService<ClipTextEngine>(),
            AppSettings.ModelsPath));
        services.AddSingleton<OllamaClient>();
        services.AddSingleton<IImageUnderstandingService, LlavaService>();
        services.AddScoped<IImportService, ImportService>();
        services.AddSingleton<IDriveService, DriveService>();    //  ← NEW

        // ── ViewModels ──────────────────────────────────────────────
        services.AddTransient<MainViewModel>();
        services.AddTransient<ScanDrivesViewModel>();             //  ← NEW

        // ── Views ───────────────────────────────────────────────────
        services.AddTransient<ScanDrivesWindow>();                //  ← NEW

        services.AddScoped<IExclusionRepository, ExclusionRepository>();

        Services = services.BuildServiceProvider();

        // Ensure database exists
        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<PhotoIQContext>().Database.EnsureCreated();
    }
}
