using Svg;
using System.Drawing;
using System.Drawing.Imaging;

// Resolve paths relative to this project file's location
var brandingDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
var svgPath     = Path.Combine(brandingDir, "icon.svg");
var icoPath     = Path.Combine(brandingDir, "icon.ico");
var pngDir      = brandingDir;

Console.WriteLine($"Source SVG : {svgPath}");
Console.WriteLine($"Output ICO : {icoPath}");

if (!File.Exists(svgPath))
{
    Console.Error.WriteLine("ERROR: icon.svg not found. Run this from the branding/ directory.");
    Environment.Exit(1);
}

// Render SVG at all required icon sizes
int[] sizes = [256, 128, 64, 48, 32, 24, 16];
var bitmaps  = new List<(int Size, Bitmap Bmp)>();

var doc = SvgDocument.Open(svgPath);

foreach (var size in sizes)
{
    var bmp = doc.Draw(size, size);
    bitmaps.Add((size, bmp));

    // Also save standalone PNGs (useful for Store / web assets)
    var pngPath = Path.Combine(pngDir, $"icon-{size}.png");
    bmp.Save(pngPath, ImageFormat.Png);
    Console.WriteLine($"  Rendered {size,3}×{size,-3}  → {Path.GetFileName(pngPath)}");
}

// Write .ico (ICONDIRENTRY + raw PNG data for each size)
WriteIco(bitmaps.Select(x => x.Bmp).ToList(), icoPath);
Console.WriteLine($"\nDone — icon.ico written ({new FileInfo(icoPath).Length / 1024} KB)");

foreach (var (_, bmp) in bitmaps) bmp.Dispose();

// ── ICO writer ────────────────────────────────────────────────────────────────
// ICO format: 6-byte ICONDIR header + count × 16-byte ICONDIRENTRY + PNG blobs.
// Using PNG-inside-ICO (supported since Vista) avoids lossy palette reduction.
static void WriteIco(IReadOnlyList<Bitmap> bitmaps, string path)
{
    // Encode each bitmap as PNG in memory first so we know the byte lengths.
    var pngBlobs = new List<byte[]>(bitmaps.Count);
    foreach (var bmp in bitmaps)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        pngBlobs.Add(ms.ToArray());
    }

    using var fs     = new FileStream(path, FileMode.Create, FileAccess.Write);
    using var writer = new BinaryWriter(fs);

    int count      = bitmaps.Count;
    int dataOffset = 6 + count * 16;   // header + all directory entries

    // ICONDIR
    writer.Write((ushort)0);      // idReserved
    writer.Write((ushort)1);      // idType  = 1 (ICO)
    writer.Write((ushort)count);  // idCount

    // ICONDIRENTRY × count
    for (int i = 0; i < count; i++)
    {
        int w = bitmaps[i].Width;
        int h = bitmaps[i].Height;
        writer.Write((byte)(w  >= 256 ? 0 : w));   // bWidth  (0 encodes 256)
        writer.Write((byte)(h  >= 256 ? 0 : h));   // bHeight
        writer.Write((byte)0);                      // bColorCount
        writer.Write((byte)0);                      // bReserved
        writer.Write((ushort)1);                    // wPlanes
        writer.Write((ushort)32);                   // wBitCount
        writer.Write((uint)pngBlobs[i].Length);     // dwBytesInRes
        writer.Write((uint)dataOffset);             // dwImageOffset
        dataOffset += pngBlobs[i].Length;
    }

    // Image data
    foreach (var blob in pngBlobs)
        writer.Write(blob);
}
