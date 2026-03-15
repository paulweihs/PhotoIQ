using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhotoIQPro.AI;

/// <summary>
/// CLIP BPE tokenizer compatible with openai/clip-vit-base-patch32.
/// Requires vocab.json and merges.txt from the model directory.
/// These files ship alongside HuggingFace CLIP model downloads.
/// </summary>
public sealed class ClipTokenizer
{
    private readonly Dictionary<string, int> _encoder;
    private readonly Dictionary<(string, string), int> _bpeRanks;
    private readonly Dictionary<int, char> _byteEncoder;
    private readonly Dictionary<string, string[]> _cache = new();

    private const int StartToken = 49406;
    private const int EndToken = 49407;
    private const int ContextLength = 77;

    // Matches the same token categories as OpenAI's CLIP tokenizer.
    private static readonly Regex Pat = new(
        @"<\|startoftext\|>|<\|endoftext\|>|'s|'t|'re|'ve|'m|'ll|'d|[\p{L}]+|[\p{N}]|[^\s\p{L}\p{N}]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public ClipTokenizer(string modelsPath)
    {
        var vocabPath = Path.Combine(modelsPath, "vocab.json");
        if (!File.Exists(vocabPath))
            throw new FileNotFoundException($"CLIP vocabulary not found at: {vocabPath}");

        _encoder = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(vocabPath))!;

        var mergesPath = Path.Combine(modelsPath, "merges.txt");
        if (!File.Exists(mergesPath))
            throw new FileNotFoundException($"CLIP merges file not found at: {mergesPath}");

        _bpeRanks = new Dictionary<(string, string), int>();
        int rank = 0;
        foreach (var line in File.ReadLines(mergesPath).Skip(1)) // first line is a header
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(' ');
            if (parts.Length == 2) _bpeRanks[(parts[0], parts[1])] = rank++;
        }

        _byteEncoder = BuildByteEncoder();
    }

    /// <summary>
    /// Encodes a text prompt into a fixed-length int[77] token array
    /// with start/end tokens and zero-padding, matching CLIP's input format.
    /// </summary>
    public int[] Encode(string text)
    {
        text = text.Trim().ToLowerInvariant();

        var ids = new List<int>(ContextLength) { StartToken };

        foreach (Match match in Pat.Matches(text))
        {
            // Map UTF-8 bytes to the GPT-2 Unicode byte encoding.
            var bytes = Encoding.UTF8.GetBytes(match.Value);
            var word = string.Concat(bytes.Select(b => _byteEncoder[b]));

            foreach (var piece in Bpe(word))
                if (_encoder.TryGetValue(piece, out int id))
                    ids.Add(id);
        }

        ids.Add(EndToken);

        // Truncate to context length (EOS always occupies last slot if text is long).
        var result = new int[ContextLength];
        int copy = Math.Min(ids.Count, ContextLength);
        for (int i = 0; i < copy; i++) result[i] = ids[i];
        // If truncated, ensure EOS is in the last position.
        if (copy == ContextLength) result[ContextLength - 1] = EndToken;
        return result;
    }

    private string[] Bpe(string word)
    {
        if (_cache.TryGetValue(word, out var cached)) return cached;

        // Build initial character list; the last character gets the word-boundary suffix.
        var chars = new List<string>(word.Length);
        for (int i = 0; i < word.Length - 1; i++) chars.Add(word[i].ToString());
        chars.Add(word[^1] + "</w>");

        while (chars.Count > 1)
        {
            // Find the pair with the lowest BPE rank (highest priority merge).
            int bestRank = int.MaxValue, bestIdx = -1;
            for (int i = 0; i < chars.Count - 1; i++)
                if (_bpeRanks.TryGetValue((chars[i], chars[i + 1]), out int r) && r < bestRank)
                { bestRank = r; bestIdx = i; }

            if (bestIdx < 0) break;

            chars[bestIdx] += chars[bestIdx + 1];
            chars.RemoveAt(bestIdx + 1);
        }

        var result = chars.ToArray();
        _cache[word] = result;
        return result;
    }

    /// <summary>
    /// Replicates GPT-2's byte-to-unicode mapping so every byte value has
    /// a printable Unicode representation.
    /// </summary>
    private static Dictionary<int, char> BuildByteEncoder()
    {
        // Printable ranges that map to themselves.
        var printable = Enumerable.Range('!', '~' - '!' + 1)       // 33–126
            .Concat(Enumerable.Range('¡', '¬' - '¡' + 1))          // 161–172
            .Concat(Enumerable.Range('®', 'ÿ' - '®' + 1))          // 174–255
            .ToHashSet();

        var d = new Dictionary<int, char>(256);
        int n = 0;
        for (int b = 0; b < 256; b++)
            d[b] = printable.Contains(b) ? (char)b : (char)(256 + n++);
        return d;
    }
}
