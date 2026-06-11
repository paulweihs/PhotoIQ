using System;
using System.IO;
using System.Runtime.InteropServices;

namespace PhotoWell.Desktop.ViewModels;

internal static class NativeMethods
{
	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct OpenAsInfo
	{
		[MarshalAs(UnmanagedType.LPWStr)]
		public string? pcszFile;

		[MarshalAs(UnmanagedType.LPWStr)]
		public string? pcszClass;

		public int oaifInFlags;
	}

	private struct MapiMessage
	{
		public int Reserved;

		public string? Subject;

		public string? NoteText;

		public string? MessageType;

		public string? DateReceived;

		public string? ConversationID;

		public int Flags;

		public nint Originator;

		public int RecipCount;

		public nint Recips;

		public int FileCount;

		public nint Files;
	}

	private struct MapiFileDesc
	{
		public int Reserved;

		public int Flags;

		public int Position;

		[MarshalAs(UnmanagedType.LPStr)]
		public string Path;

		[MarshalAs(UnmanagedType.LPStr)]
		public string FileName;

		public nint FileType;
	}

	private const int OAIF_ALLOW_REGISTRATION = 1;

	private const int OAIF_EXEC = 4;

	private const int MAPI_LOGON_UI = 1;

	private const int MAPI_DIALOG = 8;

	[DllImport("user32.dll", CharSet = CharSet.Auto)]
	internal static extern int SystemParametersInfo(int uAction, int uParam, string lpvParam, int fuWinIni);

	[DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern int SHOpenWithDialog(nint hwndParent, ref OpenAsInfo oai);

	internal static void ShowOpenWithDialog(nint hwnd, string filePath)
	{
		OpenAsInfo oai = new OpenAsInfo
		{
			pcszFile = filePath,
			pcszClass = null,
			oaifInFlags = 5
		};
		SHOpenWithDialog(hwnd, ref oai);
	}

	[DllImport("MAPI32.DLL", CharSet = CharSet.Ansi)]
	private static extern int MAPISendMail(nint session, nint hwnd, ref MapiMessage message, int flags, int reserved);

	internal static void MapiSendFile(string filePath, string subject)
		=> MapiSendFiles(new[] { filePath }, subject);

	/// <summary>
	/// Opens the default mail client's compose window with the given files attached.
	/// No recipient is pre-filled; the user addresses and sends the email themselves.
	/// Blocks until the compose dialog is dismissed — call from a background thread.
	/// </summary>
	internal static void MapiSendFiles(IReadOnlyList<string> filePaths, string subject)
	{
		int size = Marshal.SizeOf<MapiFileDesc>();
		nint filesPtr = Marshal.AllocHGlobal(size * filePaths.Count);
		int written = 0;
		try
		{
			for (int i = 0; i < filePaths.Count; i++)
			{
				MapiFileDesc structure = new MapiFileDesc
				{
					Reserved = 0,
					Flags = 0,
					Position = -1,
					Path = filePaths[i],
					FileName = Path.GetFileName(filePaths[i]),
					FileType = IntPtr.Zero
				};
				Marshal.StructureToPtr(structure, filesPtr + i * size, fDeleteOld: false);
				written++;
			}
			MapiMessage message = new MapiMessage
			{
				Subject = subject,
				FileCount = filePaths.Count,
				Files = filesPtr
			};
			int num2 = MAPISendMail(IntPtr.Zero, IntPtr.Zero, ref message, MAPI_LOGON_UI | MAPI_DIALOG, 0);
			if (num2 > 1)
			{
				throw new InvalidOperationException($"MAPISendMail returned error code {num2}.");
			}
		}
		finally
		{
			// StructureToPtr allocates unmanaged copies of the LPStr fields.
			for (int i = 0; i < written; i++)
			{
				Marshal.DestroyStructure<MapiFileDesc>(filesPtr + i * size);
			}
			Marshal.FreeHGlobal(filesPtr);
		}
	}
}
