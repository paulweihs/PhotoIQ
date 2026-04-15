using System.Buffers.Binary;

namespace PhotoIQPro.Services.Import;

/// <summary>
/// Extracts the largest embedded JPEG preview from TIFF-based RAW files
/// (DNG, CR2, NEF, ARW) without requiring any external codec or OS extension.
///
/// RAW files are TIFF containers with multiple Image File Directories (IFDs).
/// IFD0 holds a small thumbnail (~256px). A SubIFD or linked IFD holds the
/// full-size JPEG preview that the camera or DNG converter embedded. This class
/// walks the TIFF structure to find and return that preview.
///
/// Works for: DNG, CR2, NEF, ARW, and any other TIFF-based RAW format.
/// Does NOT work for CR3 (ISO Base Media / MP4 container) — those fall through
/// to the WIC path.
/// </summary>
public static class RawPreviewExtractor
{
    // TIFF tag IDs relevant to finding embedded JPEG previews
    private const ushort TagImageWidth               = 0x0100;
    private const ushort TagImageLength              = 0x0101;
    private const ushort TagCompression              = 0x0103;
    private const ushort TagSubIFDs                  = 0x014A;
    private const ushort TagJpegInterchangeFormat    = 0x0201; // offset of embedded JPEG data
    private const ushort TagJpegInterchangeFormatLen = 0x0202; // byte length of embedded JPEG

    // Compression value 6 = "old-style JPEG" used for embedded previews in RAW files.
    // (Not the same as the raw sensor data compression which uses values like 34892.)
    private const ushort CompressionJpeg = 6;

    /// <summary>
    /// Returns the raw bytes of the largest embedded JPEG preview found in the file,
    /// or null if none is found or the file is not a supported TIFF-based RAW.
    /// </summary>
    public static byte[]? ExtractLargestJpegPreview(string filePath)
    {
        try
        {
            using var fs  = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var rdr = new BinaryReader(fs);

            // ── TIFF header (8 bytes) ────────────────────────────────────────────
            ushort byteOrder = rdr.ReadUInt16();
            bool   le        = byteOrder == 0x4949; // 'II' = little-endian, 'MM' = big-endian
            if (byteOrder != 0x4949 && byteOrder != 0x4D4D) return null; // not TIFF

            if (ReadU16(rdr, le) != 42) return null; // not standard TIFF (BigTIFF uses 43)

            uint firstIfd = ReadU32(rdr, le);

            // ── Collect preview candidates from all IFDs ─────────────────────────
            var candidates = new List<(int width, int height, long offset, int length)>();
            var visited    = new HashSet<long>(); // guard against circular IFD chains

            ScanIfd(fs, rdr, le, firstIfd, candidates, visited);

            if (candidates.Count == 0) return null;

            // Pick the candidate with the largest area (most image data).
            // Use byte length as tiebreaker when dimensions aren't recorded in the IFD.
            var (_, _, jpegOff, jpegLen) = candidates
                .OrderByDescending(c => (long)c.width * c.height)
                .ThenByDescending(c => c.length)
                .First();

            if (jpegOff <= 0 || jpegOff + jpegLen > fs.Length) return null;

            fs.Position = jpegOff;
            var bytes = rdr.ReadBytes(jpegLen);

            // Verify JPEG SOI marker — guards against corrupt offset tables
            return bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xD8 ? bytes : null;
        }
        catch { return null; }
    }

    // ── TIFF IFD walker ─────────────────────────────────────────────────────────

    private static void ScanIfd(
        FileStream fs, BinaryReader rdr, bool le, long ifdOffset,
        List<(int, int, long, int)> candidates, HashSet<long> visited)
    {
        if (ifdOffset == 0 || ifdOffset + 2 > fs.Length || !visited.Add(ifdOffset)) return;

        fs.Position = ifdOffset;
        int entryCount = ReadU16(rdr, le);
        if (entryCount is <= 0 or > 512) return; // sanity guard

        int    width       = 0;
        int    height      = 0;
        long   jpegOff     = 0;
        int    jpegLen     = 0;
        ushort compression = 0;
        var    subIfds     = new List<long>();

        for (int i = 0; i < entryCount; i++)
        {
            if (fs.Position + 12 > fs.Length) break;

            ushort tag   = ReadU16(rdr, le);
            ushort type  = ReadU16(rdr, le);
            uint   count = ReadU32(rdr, le);
            uint   raw   = ReadU32(rdr, le); // inline value or file offset to data
            uint   value = ScalarValue(type, raw, le);

            switch (tag)
            {
                case TagImageWidth:               width       = (int)value;    break;
                case TagImageLength:              height      = (int)value;    break;
                case TagCompression:              compression = (ushort)value; break;
                case TagJpegInterchangeFormat:    jpegOff     = value;         break;
                case TagJpegInterchangeFormatLen: jpegLen     = (int)value;    break;

                case TagSubIFDs:
                    // value / raw is either the inline offset (count=1) or a pointer
                    // to an array of offsets (count>1).
                    long resume = fs.Position;
                    if (count == 1)
                    {
                        subIfds.Add(value);
                    }
                    else
                    {
                        fs.Position = raw; // raw = file offset to LONG array
                        for (uint s = 0; s < Math.Min(count, 16u); s++)
                        {
                            if (fs.Position + 4 > fs.Length) break;
                            subIfds.Add(ReadU32(rdr, le));
                        }
                        fs.Position = resume;
                    }
                    break;
            }
        }

        // A valid embedded JPEG preview has compression=6, a non-zero offset,
        // and is at least 4 KB (ruling out degenerate/corrupt entries).
        if (compression == CompressionJpeg && jpegOff > 0 && jpegLen >= 4096)
            candidates.Add((width, height, jpegOff, jpegLen));

        // Follow the linked IFD chain (IFD0 → IFD1 → IFD2 …)
        if (fs.Position + 4 <= fs.Length)
        {
            uint nextIfd = ReadU32(rdr, le);
            ScanIfd(fs, rdr, le, nextIfd, candidates, visited);
        }

        // Recurse into SubIFDs
        foreach (long sub in subIfds)
            ScanIfd(fs, rdr, le, sub, candidates, visited);
    }

    // ── Endian-aware readers ─────────────────────────────────────────────────────

    // Extracts the scalar uint32 value from an IFD entry's 4-byte value field.
    // SHORT (type 3): the 2-byte value is packed into the first bytes of the field.
    // LE TIFF SHORT: [L, H, 0, 0] → raw after ReadU32(le) = 0x0000_HHLL → & 0xFFFF
    // BE TIFF SHORT: [H, L, 0, 0] → raw after ReadU32(be) = 0xHHLL_0000 → >> 16
    private static uint ScalarValue(ushort type, uint raw, bool le) => type switch
    {
        3 => le ? (raw & 0xFFFF) : (raw >> 16), // SHORT
        _ => raw                                  // LONG (4), IFD (13), or other
    };

    private static ushort ReadU16(BinaryReader r, bool le)
    {
        ushort v = r.ReadUInt16();
        return le ? v : BinaryPrimitives.ReverseEndianness(v);
    }

    private static uint ReadU32(BinaryReader r, bool le)
    {
        uint v = r.ReadUInt32();
        return le ? v : BinaryPrimitives.ReverseEndianness(v);
    }
}
