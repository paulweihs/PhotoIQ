using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using PhotoWell.Common;
using PhotoWell.Core.Interfaces;

namespace PhotoWell.Services.ExifTool;

public class ExifEditService : IExifEditService
{
    private readonly string _exifToolPath;
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(2) };

    public ExifEditService(string exifToolPath) => _exifToolPath = exifToolPath;

    public bool IsAvailable => File.Exists(_exifToolPath);

    public async Task EnsureAvailableAsync(IProgress<string>? status = null, CancellationToken ct = default)
    {
        if (IsAvailable) return;

        Directory.CreateDirectory(Path.GetDirectoryName(_exifToolPath)!);

        status?.Report("Checking ExifTool version…");
        var version = (await _http.GetStringAsync(AppSettings.ExifToolVersionUrl, ct)).Trim();
        var zipUrl = $"https://exiftool.org/exiftool-{version}_64.zip";

        status?.Report($"Downloading ExifTool {version}…");
        var zipBytes = await _http.GetByteArrayAsync(zipUrl, ct);

        status?.Report("Installing ExifTool…");
        using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        var entry = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("ExifTool executable not found in downloaded ZIP.");
        entry.ExtractToFile(_exifToolPath, overwrite: true);

        status?.Report("ExifTool ready.");
    }

    public async Task WriteTagAsync(string filePath, string tag, string value, CancellationToken ct = default)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("ExifTool is not available — call EnsureAvailableAsync first.");

        var escaped = value.Replace("\"", "\\\"");
        var psi = new ProcessStartInfo(
            _exifToolPath,
            $"-{tag}=\"{escaped}\" -overwrite_original_in_place \"{filePath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ExifTool process.");
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
        {
            var err = await proc.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException($"ExifTool exited {proc.ExitCode}: {err.Trim()}");
        }
    }
}
