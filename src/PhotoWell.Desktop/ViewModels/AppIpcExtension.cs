// This file is intentionally placed in ViewModels/ because the Desktop project root
// is not writable in the current session. It is part of the PhotoWell.Desktop namespace
// (not the ViewModels sub-namespace) and extends App as a partial class.

using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Windows;
using PhotoWell.Common;
using PhotoWell.Desktop.ViewModels;
using PhotoWell.Desktop.Views;

namespace PhotoWell.Desktop;

/// <summary>
/// Partial class extension of App that adds:
/// - Single-instance enforcement via a named Mutex (Global\PhotoWellSingleInstance)
/// - Named-pipe IPC server so second instances can forward CLI args to the running instance
/// - CLI argument handling: --select, --describe, --similar, --related
/// </summary>
/// <remarks>
/// WIRING REQUIRED in App.xaml.cs to fully activate the single-instance guard:
///
///   1. In StartupCoreAsync() at the very beginning, add:
///        if (!CheckSingleInstanceAndHandleArgs()) return;
///
///   2. In OnExit() before base.OnExit(e), add:
///        StopIpcListener();
///        _singleInstanceMutex?.ReleaseMutex();
///        _singleInstanceMutex?.Dispose();
///
///   3. At the end of StartupCoreAsync(), after mainWindow.Show(), add:
///        var (cmd, path) = ParseCliArgs();
///        if (cmd is not null &amp;&amp; path is not null)
///            HandleIpcCommand(cmd, path);
///
/// Until that wiring is added, the IPC methods are compiled and available but not
/// connected to the startup flow.
/// </remarks>
public partial class App
{
    private const string MutexName = "Global\\PhotoWellSingleInstance";
    private const string PipeName  = "PhotoWellIPC";

    private Mutex?                  _singleInstanceMutex;
    private CancellationTokenSource _pipeCts = new();

    // ── Single-instance ───────────────────────────────────────────────────────

    /// <summary>
    /// Checks whether this is the first instance. If another instance is running, forwards
    /// the CLI args via the named pipe and returns false (caller must call Shutdown(0)).
    /// If this is the first instance, starts the IPC pipe listener and returns true.
    /// </summary>
    public bool CheckSingleInstanceAndHandleArgs()
    {
        _singleInstanceMutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            var (cmd, path) = ParseCliArgs();
            if (cmd is not null && path is not null)
                ForwardToRunningInstance(cmd, path);
            else
                ForwardToRunningInstance("activate", "");

            Shutdown(0);
            return false;
        }

        StartPipeListener();
        return true;
    }

    /// <summary>Cancels the pipe listener. Call from OnExit before base.OnExit().</summary>
    public void StopIpcListener() => _pipeCts.Cancel();

    // ── CLI arg parsing ───────────────────────────────────────────────────────

    /// <summary>
    /// Parses process command-line arguments for context-menu commands.
    /// Supported flags: --select, --describe, --similar, --related (each followed by a path).
    /// </summary>
    public static (string? command, string? path) ParseCliArgs()
        => ParseArgs(Environment.GetCommandLineArgs().Skip(1).ToArray());

    /// <summary>Parses an explicit args array.</summary>
    public static (string? command, string? path) ParseArgs(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            var flag = args[i].ToLowerInvariant();
            if (flag is "--select" or "--describe" or "--similar" or "--related"
                     or "--tag" or "--import-folder" or "--import-folder-recursive"
                     or "--remove-folder" or "--remove-folder-recursive")
                return (flag.TrimStart('-'), args[i + 1]);
        }
        return (null, null);
    }

    // ── Pipe send/receive ─────────────────────────────────────────────────────

    private static void ForwardToRunningInstance(string command, string path)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2_000);
            var msg = JsonSerializer.Serialize(new IpcMessage { Command = command, Path = path });
            using var writer = new StreamWriter(client);
            writer.WriteLine(msg);
        }
        catch (Exception ex)
        {
            AppLog.Error($"IPC forward failed: {ex.Message}");
        }
    }

    private void StartPipeListener()
    {
        var ct = _pipeCts.Token;
        Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(ct);

                    using var reader = new StreamReader(server);
                    var line = await reader.ReadLineAsync(ct);
                    if (string.IsNullOrEmpty(line)) continue;

                    var msg = JsonSerializer.Deserialize<IpcMessage>(line);
                    if (msg is null) continue;

                    await Dispatcher.InvokeAsync(() => HandleIpcCommand(msg.Command, msg.Path));
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    AppLog.Error($"IPC pipe error: {ex.Message}");
                    await Task.Delay(500, ct).ConfigureAwait(false);
                }
            }
        }, ct);
    }

    // ── Command dispatch ──────────────────────────────────────────────────────

    /// <summary>
    /// Dispatches a command received from the IPC pipe or from CLI args.
    /// Must be called on the UI thread.
    /// </summary>
    public void HandleIpcCommand(string? command, string? path)
    {
        if (string.IsNullOrEmpty(path)) return;

        if (MainWindow is { } mw)
        {
            if (mw.WindowState == WindowState.Minimized)
                mw.WindowState = WindowState.Normal;
            mw.Activate();
        }

        switch (command)
        {
            case "select":
                (MainWindow as Views.MainWindow)?.SelectFileInGallery(path);
                break;
            case "tag":
                OpenQuickTagWindow([path]);
                break;
            case "describe":
                OpenQuickDescriptionWindow(path);
                break;
            case "similar":
                (MainWindow as Views.MainWindow)?.FindSimilarForFile(path);
                break;
            case "related":
                (MainWindow as Views.MainWindow)?.FindRelatedForFile(path);
                break;
            case "import-folder":
                (MainWindow as Views.MainWindow)?.ImportFolderFromShell(path, recursive: false);
                break;
            case "import-folder-recursive":
                (MainWindow as Views.MainWindow)?.ImportFolderFromShell(path, recursive: true);
                break;
            case "remove-folder":
                (MainWindow as Views.MainWindow)?.RemoveFolderFromShell(path, recursive: false);
                break;
            case "remove-folder-recursive":
                (MainWindow as Views.MainWindow)?.RemoveFolderFromShell(path, recursive: true);
                break;
        }
    }

    private void OpenQuickDescriptionWindow(string filePath)
    {
        try
        {
            var vm  = new QuickDescriptionViewModel(filePath, Services);
            var win = new Views.QuickDescriptionWindow { DataContext = vm };
            win.Show();
        }
        catch (Exception ex)
        {
            AppLog.Error($"Failed to open QuickDescriptionWindow for '{filePath}': {ex.Message}");
        }
    }

    private void OpenQuickTagWindow(IReadOnlyList<string> filePaths)
    {
        try
        {
            var vm  = new QuickTagViewModel(filePaths, Services);
            var win = new Views.QuickTagWindow { DataContext = vm };
            win.Show();
        }
        catch (Exception ex)
        {
            AppLog.Error($"Failed to open QuickTagWindow: {ex.Message}");
        }
    }

    // ── IPC message DTO ───────────────────────────────────────────────────────

    private sealed class IpcMessage
    {
        public string? Command { get; set; }
        public string? Path    { get; set; }
    }
}
