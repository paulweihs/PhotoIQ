// PhotoWell out-of-process COM shell extension.
//
// Architecture:
//   - This exe acts as a COM Local Server (LocalServer32).
//   - Explorer activates it via COM when the user right-clicks image files or folders.
//   - The extension communicates with the running PhotoWell instance via named pipe
//     (same IPC protocol defined in AppIpcExtension.cs in the main app).
//   - If PhotoWell is not running, it is launched with the appropriate CLI flag.
//
// Registration (HKCU — no admin rights needed):
//   PhotoWell.ShellExtension.exe --register   → writes registry keys
//   PhotoWell.ShellExtension.exe --unregister → removes registry keys
//
// File filter: .jpg .jpeg .png .heic .gif .bmp .webp .tif .tiff .cr2 .cr3 .nef .arw .dng .rw2 .orf .raf .raw
// Folder support: right-clicking a folder shows Add Contents / Remove Contents submenus

using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace PhotoWell.ShellExtension;

internal static class Program
{
    // COM class GUID for PhotoWellContextMenu — generated once, must not change after registration.
    internal const string ClsidString = "7E4A1D9F-3B8C-4F2A-9E6D-5C0A2B7F8D1E";

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0)
        {
            switch (args[0].ToLowerInvariant())
            {
                case "--register":
                    RegistrationHelper.Register();
                    Console.WriteLine("PhotoWell context menu registered.");
                    return 0;
                case "--unregister":
                    RegistrationHelper.Unregister();
                    Console.WriteLine("PhotoWell context menu unregistered.");
                    return 0;
                case "--embedding-test":
                    Console.WriteLine($"CLSID: {{{ClsidString}}}");
                    return 0;
            }
        }

        // Launched by COM activation — run the COM local server message loop.
        ComServer.Run();
        return 0;
    }
}

// ── COM interfaces ────────────────────────────────────────────────────────────

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("000214E8-0000-0000-C000-000000000046")]
internal interface IShellExtInit
{
    void Initialize(
        nint pidlFolder,
        IDataObject? pDataObj,
        nint hKeyProgID);
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("000214E4-0000-0000-C000-000000000046")]
internal interface IContextMenu
{
    [PreserveSig]
    int QueryContextMenu(
        nint hMenu,
        uint indexMenu,
        uint idCmdFirst,
        uint idCmdLast,
        uint uFlags);

    [PreserveSig]
    int InvokeCommand(nint pici);

    [PreserveSig]
    int GetCommandString(
        nint idCmd,
        uint uType,
        nint pReserved,
        [MarshalAs(UnmanagedType.LPStr)] StringBuilder pszName,
        uint cchMax);
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("00000001-0000-0000-C000-000000000046")]
internal interface IClassFactory
{
    [PreserveSig]
    int CreateInstance(nint pUnkOuter, ref Guid riid, out nint ppvObject);
    [PreserveSig]
    int LockServer([MarshalAs(UnmanagedType.Bool)] bool fLock);
}

// ── P/Invoke ──────────────────────────────────────────────────────────────────

internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MENUITEMINFO
    {
        public uint   cbSize;
        public uint   fMask;
        public uint   fType;
        public uint   fState;
        public uint   wID;
        public nint   hSubMenu;
        public nint   hbmpChecked;
        public nint   hbmpUnchecked;
        public nint   dwItemData;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? dwTypeData;
        public uint   cch;
        public nint   hbmpItem;
    }

    internal const uint MIIM_STATE   = 0x00000001;
    internal const uint MIIM_ID      = 0x00000002;
    internal const uint MIIM_SUBMENU = 0x00000004;
    internal const uint MIIM_TYPE    = 0x00000010;
    internal const uint MIIM_STRING  = 0x00000040;

    internal const uint MF_STRING    = 0x00000000;
    internal const uint MF_SEPARATOR = 0x00000800;

    internal const uint MFS_GRAYED   = 0x00000003;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint CreatePopupMenu();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool InsertMenuItemW(
        nint hMenu,
        uint uItem,
        [MarshalAs(UnmanagedType.Bool)] bool fByPosition,
        ref MENUITEMINFO lpmii);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyMenu(nint hMenu);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint DragQueryFileW(
        nint hDrop,
        uint iFile,
        nint lpszFile,
        uint cch);

    [DllImport("ole32.dll")]
    internal static extern int CoRegisterClassObject(
        ref Guid rclsid,
        [MarshalAs(UnmanagedType.IUnknown)] object pUnk,
        uint dwClsContext,
        uint flags,
        out uint lpdwRegister);

    [DllImport("ole32.dll")]
    internal static extern int CoRevokeClassObject(uint dwRegister);

    [DllImport("ole32.dll")]
    internal static extern int CoInitializeEx(nint pvReserved, uint dwCoInit);

    [DllImport("ole32.dll")]
    internal static extern void CoUninitialize();
}

// ── Shell extension class ─────────────────────────────────────────────────────

[ComVisible(true)]
[Guid(Program.ClsidString)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class PhotoWellContextMenu : IShellExtInit, IContextMenu
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".heic", ".gif", ".bmp", ".webp",
        ".tif", ".tiff", ".cr2", ".cr3", ".nef", ".arw", ".dng",
        ".rw2", ".orf", ".raf", ".raw"
    };

    // Image file selections.
    private readonly List<string> _selectedPaths = [];
    private bool _multiSelect;

    // Folder selections.
    private readonly List<string> _selectedFolders = [];

    // Command IDs relative to idCmdFirst.
    // Image commands (0–5).
    private const uint CMD_OPEN              = 0;
    private const uint CMD_TAG               = 1;
    private const uint CMD_ALBUM             = 2;
    private const uint CMD_RELATED           = 3;
    private const uint CMD_SIMILAR           = 4;
    private const uint CMD_DESCRIBE          = 5;
    // Folder commands (6–9).
    private const uint CMD_ADD_FOLDER        = 6;   // this folder only
    private const uint CMD_ADD_FOLDER_REC    = 7;   // folder + subfolders
    private const uint CMD_REMOVE_FOLDER     = 8;   // this folder only
    private const uint CMD_REMOVE_FOLDER_REC = 9;   // folder + subfolders
    private const uint CMD_MAX               = 9;

    // ── IShellExtInit ─────────────────────────────────────────────────────────

    void IShellExtInit.Initialize(nint pidlFolder, IDataObject? pDataObj, nint hKeyProgID)
    {
        _selectedPaths.Clear();
        _selectedFolders.Clear();
        if (pDataObj == null) return;

        var paths = DataObjectHelper.GetFilePaths(pDataObj);
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
                _selectedFolders.Add(p);
            else if (ImageExtensions.Contains(Path.GetExtension(p)))
                _selectedPaths.Add(p);
        }
        _multiSelect = _selectedPaths.Count > 1;
    }

    // ── IContextMenu ──────────────────────────────────────────────────────────

    int IContextMenu.QueryContextMenu(nint hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags)
    {
        bool hasImages  = _selectedPaths.Count  > 0;
        bool hasFolders = _selectedFolders.Count > 0;
        if (!hasImages && !hasFolders) return 0;

        var subMenu = NativeMethods.CreatePopupMenu();
        uint pos = 0;

        // ── Image commands ────────────────────────────────────────────────────
        if (hasImages)
        {
            AddMenuItem(subMenu, pos++, idCmdFirst + CMD_OPEN,    "Open in PhotoWell");
            AddMenuItem(subMenu, pos++, idCmdFirst + CMD_TAG,     "Add Tag…");
            AddMenuItem(subMenu, pos++, idCmdFirst + CMD_ALBUM,   "Add to Album…");
            AddSeparator(subMenu, pos++);
            AddMenuItem(subMenu, pos++, idCmdFirst + CMD_RELATED, "Find Related Photos");
            AddMenuItem(subMenu, pos++, idCmdFirst + CMD_SIMILAR,
                _multiSelect ? "Find Similar Photos (single file only)" : "Find Similar Photos",
                grayed: _multiSelect);
            AddSeparator(subMenu, pos++);
            AddMenuItem(subMenu, pos++, idCmdFirst + CMD_DESCRIBE,
                _multiSelect ? "Show AI Description (single file only)" : "Show AI Description",
                grayed: _multiSelect);
        }

        // ── Folder commands ───────────────────────────────────────────────────
        if (hasFolders)
        {
            if (hasImages) AddSeparator(subMenu, pos++);

            // "Add Contents" → submenu
            var addSubMenu = NativeMethods.CreatePopupMenu();
            AddMenuItem(addSubMenu, 0, idCmdFirst + CMD_ADD_FOLDER,     "This folder only");
            AddMenuItem(addSubMenu, 1, idCmdFirst + CMD_ADD_FOLDER_REC, "This folder and subfolders");
            AddPopupItem(subMenu, pos++, "Add Contents", addSubMenu);

            // "Remove Contents" → submenu
            var removeSubMenu = NativeMethods.CreatePopupMenu();
            AddMenuItem(removeSubMenu, 0, idCmdFirst + CMD_REMOVE_FOLDER,     "This folder only");
            AddMenuItem(removeSubMenu, 1, idCmdFirst + CMD_REMOVE_FOLDER_REC, "This folder and subfolders");
            AddPopupItem(subMenu, pos++, "Remove Contents", removeSubMenu);
        }

        // ── Top-level "PhotoWell" entry ───────────────────────────────────────
        var mii = new NativeMethods.MENUITEMINFO
        {
            cbSize     = (uint)Marshal.SizeOf<NativeMethods.MENUITEMINFO>(),
            fMask      = NativeMethods.MIIM_SUBMENU | NativeMethods.MIIM_STRING | NativeMethods.MIIM_TYPE,
            fType      = NativeMethods.MF_STRING,
            dwTypeData = "PhotoWell",
            hSubMenu   = subMenu,
        };
        NativeMethods.InsertMenuItemW(hMenu, indexMenu, fByPosition: true, ref mii);

        return (int)(CMD_MAX + 1);
    }

    int IContextMenu.InvokeCommand(nint pici)
    {
        int cmdOffset = Marshal.ReadInt16(pici + IntPtr.Size * 2) & 0xFFFF;

        switch ((uint)cmdOffset)
        {
            case CMD_OPEN:
                foreach (var p in _selectedPaths)
                    SendToPhotoWell("select", p);
                break;
            case CMD_TAG:
                foreach (var p in _selectedPaths)
                    SendToPhotoWell("tag", p);
                break;
            case CMD_ALBUM:
                foreach (var p in _selectedPaths)
                    SendToPhotoWell("select", p);
                break;
            case CMD_RELATED:
                if (_selectedPaths.Count == 1)
                    SendToPhotoWell("related", _selectedPaths[0]);
                break;
            case CMD_SIMILAR:
                if (_selectedPaths.Count == 1)
                    SendToPhotoWell("similar", _selectedPaths[0]);
                break;
            case CMD_DESCRIBE:
                if (_selectedPaths.Count == 1)
                    SendToPhotoWell("describe", _selectedPaths[0]);
                break;
            case CMD_ADD_FOLDER:
                foreach (var f in _selectedFolders)
                    SendToPhotoWell("import-folder", f);
                break;
            case CMD_ADD_FOLDER_REC:
                foreach (var f in _selectedFolders)
                    SendToPhotoWell("import-folder-recursive", f);
                break;
            case CMD_REMOVE_FOLDER:
                foreach (var f in _selectedFolders)
                    SendToPhotoWell("remove-folder", f);
                break;
            case CMD_REMOVE_FOLDER_REC:
                foreach (var f in _selectedFolders)
                    SendToPhotoWell("remove-folder-recursive", f);
                break;
        }

        return 0;
    }

    int IContextMenu.GetCommandString(nint idCmd, uint uType, nint pReserved, StringBuilder pszName, uint cchMax)
        => 0;

    // ── IPC helpers ───────────────────────────────────────────────────────────

    private static void SendToPhotoWell(string command, string path)
    {
        const string PipeName = "PhotoWellIPC";

        // Try the named pipe first (PhotoWell already running).
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1_000);
            var msg = JsonSerializer.Serialize(new { Command = command, Path = path });
            using var writer = new StreamWriter(client);
            writer.WriteLine(msg);
            return;
        }
        catch { /* not running — fall through to launch */ }

        // Launch PhotoWell with CLI args.
        var photoWellExe = FindPhotoWellExe("PhotoWell.exe");
        if (photoWellExe == null) return;

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName        = photoWellExe,
            Arguments       = $"--{command} \"{path}\"",
            UseShellExecute = false,
        });
    }

    private static string? FindPhotoWellExe(string exeName)
    {
        // 1. Same directory as this surrogate exe.
        var dir  = Path.GetDirectoryName(System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName);
        if (dir != null)
        {
            var local = Path.Combine(dir, exeName);
            if (File.Exists(local)) return local;
        }

        // 2. Registry: HKCU\Software\PhotoWell\InstallPath
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\PhotoWell");
        var installPath = key?.GetValue("InstallPath") as string;
        if (installPath != null)
        {
            var regPath = Path.Combine(installPath, exeName);
            if (File.Exists(regPath)) return regPath;
        }

        return null;
    }

    // ── Menu helpers ──────────────────────────────────────────────────────────

    private static void AddMenuItem(nint menu, uint pos, uint id, string text, bool grayed = false)
    {
        var mii = new NativeMethods.MENUITEMINFO
        {
            cbSize     = (uint)Marshal.SizeOf<NativeMethods.MENUITEMINFO>(),
            fMask      = NativeMethods.MIIM_ID | NativeMethods.MIIM_STRING | NativeMethods.MIIM_STATE,
            fType      = NativeMethods.MF_STRING,
            fState     = grayed ? NativeMethods.MFS_GRAYED : 0u,
            wID        = id,
            dwTypeData = text,
        };
        NativeMethods.InsertMenuItemW(menu, pos, fByPosition: true, ref mii);
    }

    private static void AddSeparator(nint menu, uint pos)
    {
        var mii = new NativeMethods.MENUITEMINFO
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.MENUITEMINFO>(),
            fMask  = NativeMethods.MIIM_TYPE,
            fType  = NativeMethods.MF_SEPARATOR,
        };
        NativeMethods.InsertMenuItemW(menu, pos, fByPosition: true, ref mii);
    }

    private static void AddPopupItem(nint menu, uint pos, string text, nint subMenu)
    {
        var mii = new NativeMethods.MENUITEMINFO
        {
            cbSize     = (uint)Marshal.SizeOf<NativeMethods.MENUITEMINFO>(),
            fMask      = NativeMethods.MIIM_SUBMENU | NativeMethods.MIIM_STRING | NativeMethods.MIIM_TYPE,
            fType      = NativeMethods.MF_STRING,
            dwTypeData = text,
            hSubMenu   = subMenu,
        };
        NativeMethods.InsertMenuItemW(menu, pos, fByPosition: true, ref mii);
    }
}

// ── IDataObject helper ────────────────────────────────────────────────────────

internal static class DataObjectHelper
{
    private const uint CF_HDROP = 15;

    public static List<string> GetFilePaths(IDataObject dataObject)
    {
        var paths = new List<string>();

        var fmt = new FORMATETC
        {
            cfFormat = (short)CF_HDROP,
            ptd      = nint.Zero,
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex   = -1,
            tymed    = TYMED.TYMED_HGLOBAL,
        };

        try
        {
            dataObject.GetData(ref fmt, out var medium);
            var hDrop = medium.unionmember;
            if (hDrop == nint.Zero) return paths;

            var count = NativeMethods.DragQueryFileW(hDrop, 0xFFFFFFFF, nint.Zero, 0);
            for (uint i = 0; i < count; i++)
            {
                uint needed = NativeMethods.DragQueryFileW(hDrop, i, nint.Zero, 0) + 1;
                var buf = Marshal.AllocHGlobal((int)(needed * 2));
                try
                {
                    NativeMethods.DragQueryFileW(hDrop, i, buf, needed);
                    var s = Marshal.PtrToStringUni(buf);
                    if (s != null) paths.Add(s);
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        catch { /* ignore — IDataObject may not support HDROP */ }

        return paths;
    }
}

// ── IClassFactory implementation ──────────────────────────────────────────────

[ComVisible(true)]
internal sealed class PhotoWellClassFactory : IClassFactory
{
    public int LockCount;

    int IClassFactory.CreateInstance(nint pUnkOuter, ref Guid riid, out nint ppvObject)
    {
        ppvObject = nint.Zero;
        if (pUnkOuter != nint.Zero) return unchecked((int)0x80040110); // CLASS_E_NOAGGREGATION
        var obj = new PhotoWellContextMenu();
        ppvObject = Marshal.GetComInterfaceForObject<PhotoWellContextMenu, IContextMenu>(obj);
        return 0;
    }

    int IClassFactory.LockServer([MarshalAs(UnmanagedType.Bool)] bool fLock)
    {
        if (fLock) System.Threading.Interlocked.Increment(ref LockCount);
        else       System.Threading.Interlocked.Decrement(ref LockCount);
        return 0;
    }
}

// ── COM local server ──────────────────────────────────────────────────────────

internal static class ComServer
{
    private const uint CLSCTX_LOCAL_SERVER  = 4;
    private const uint REGCLS_MULTIPLEUSE   = 1;
    private const uint COINIT_APARTMENTTHREADED = 2;

    public static void Run()
    {
        NativeMethods.CoInitializeEx(nint.Zero, COINIT_APARTMENTTHREADED);
        try
        {
            var clsid   = new Guid(Program.ClsidString);
            var factory = new PhotoWellClassFactory();
            NativeMethods.CoRegisterClassObject(ref clsid, factory, CLSCTX_LOCAL_SERVER, REGCLS_MULTIPLEUSE, out uint cookie);

            // Pump until Explorer releases all references (or 60 s idle timeout).
            var deadline = DateTime.UtcNow.AddMinutes(1);
            while (factory.LockCount >= 0 && DateTime.UtcNow < deadline)
                System.Threading.Thread.Sleep(100);

            NativeMethods.CoRevokeClassObject(cookie);
        }
        finally
        {
            NativeMethods.CoUninitialize();
        }
    }
}
