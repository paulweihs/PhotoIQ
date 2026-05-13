// RegistrationHelper — writes and removes HKCU registry keys needed for the
// Windows 10 classic COM context menu extension.
//
// All keys are under HKCU\Software\Classes so no administrator rights are required.
//
// Windows 11 sparse-package / IExplorerCommand registration is handled separately
// by the installer (package manifest + sparse package API) and is not covered here.

using Microsoft.Win32;

namespace PhotoWell.ShellExtension;

internal static class RegistrationHelper
{
    private const string Clsid       = Program.ClsidString;
    private const string FriendlyName = "PhotoWell Context Menu";

    // Image extensions that trigger the context menu.
    private static readonly string[] ImageExtensions =
    [
        ".jpg", ".jpeg", ".png", ".heic", ".gif", ".bmp", ".webp",
        ".tif", ".tiff", ".cr2", ".cr3", ".nef", ".arw", ".dng",
        ".rw2", ".orf", ".raf", ".raw"
    ];

    public static void Register()
    {
        var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                      ?? throw new InvalidOperationException("Cannot determine exe path.");

        // ── CLSID entry (COM Local Server) ───────────────────────────────────
        // HKCU\Software\Classes\CLSID\{GUID}
        using (var clsidKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\CLSID\{Clsid}"))
        {
            clsidKey.SetValue("", FriendlyName);

            // LocalServer32 — points to this exe (out-of-process COM server).
            using var ls = clsidKey.CreateSubKey("LocalServer32");
            ls.SetValue("", $"\"{exePath}\"");
        }

        // ── Shell extension approval ──────────────────────────────────────────
        // Windows requires shell extensions to be listed under Approved for 32/64-bit.
        using (var approved = Registry.CurrentUser.CreateSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved"))
        {
            approved.SetValue(Clsid, FriendlyName);
        }

        // ── Register for each image extension ────────────────────────────────
        // HKCU\Software\Classes\<ext>\shellex\ContextMenuHandlers\PhotoWell
        foreach (var ext in ImageExtensions)
        {
            var keyPath = $@"Software\Classes\{ext}\shellex\ContextMenuHandlers\PhotoWell";
            using var key = Registry.CurrentUser.CreateSubKey(keyPath);
            key.SetValue("", Clsid);
        }

        // ── Register for folders ──────────────────────────────────────────────
        // HKCU\Software\Classes\Directory\shellex\ContextMenuHandlers\PhotoWell
        using (var dirKey = Registry.CurrentUser.CreateSubKey(
            $@"Software\Classes\Directory\shellex\ContextMenuHandlers\PhotoWell"))
        {
            dirKey.SetValue("", Clsid);
        }

        // ── Store install path for the main app to find ──────────────────────
        var installDir = System.IO.Path.GetDirectoryName(exePath) ?? "";
        using var pwKey = Registry.CurrentUser.CreateSubKey(@"Software\PhotoWell");
        pwKey.SetValue("InstallPath", installDir);
    }

    public static void Unregister()
    {
        // Remove per-extension handlers.
        foreach (var ext in ImageExtensions)
        {
            Registry.CurrentUser.DeleteSubKeyTree(
                $@"Software\Classes\{ext}\shellex\ContextMenuHandlers\PhotoWell",
                throwOnMissingSubKey: false);
        }

        // Remove folder handler.
        Registry.CurrentUser.DeleteSubKeyTree(
            $@"Software\Classes\Directory\shellex\ContextMenuHandlers\PhotoWell",
            throwOnMissingSubKey: false);

        // Remove shell extension approval.
        using (var approved = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved", writable: true))
        {
            approved?.DeleteValue(Clsid, throwOnMissingValue: false);
        }

        // Remove CLSID entry.
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\CLSID\{Clsid}",
            throwOnMissingSubKey: false);
    }
}
