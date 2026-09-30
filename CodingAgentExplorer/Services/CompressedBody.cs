using System.Buffers;
using System.IO.Compression;
using System.Text;

namespace CodingAgentExplorer.Services;

/// <summary>
/// A captured request or response body, held Brotli-compressed.
/// </summary>
/// <remarks>
/// Bodies are the only part of a request worth compressing: they dominate the
/// <see cref="RequestStore"/> budget, and they are individually large enough to land on the
/// large-object heap, which is never compacted. SSE event data is left alone, being many
/// strings that are each far too small to reach that threshold.
/// </remarks>
internal readonly struct CompressedBody
{
    // These payloads are heavily repetitive JSON, so quality 4 already gets about 4.4x for
    // roughly 1.4 ms on a median body. Quality 11 buys a further 7% and costs 190 ms, which is
    // far too much to spend on the request path.
    private const int Quality = 4;
    private const int Window = 22;

    private readonly byte[]? _packed;

    // Kept so that unpacking is a single decode into an exactly sized buffer.
    private readonly int _utf8Length;

    /// <summary>UTF-16 length of the original text. Free to read, unlike <see cref="Text"/>.</summary>
    public int Chars { get; }

    private CompressedBody(byte[]? packed, int utf8Length, int chars)
    {
        _packed = packed;
        _utf8Length = utf8Length;
        Chars = chars;
    }

    public static CompressedBody From(string? text)
    {
        // Empty round-trips as itself rather than as a brotli frame, which keeps the MCP
        // keep-alive path (GET / with no body) free.
        if (string.IsNullOrEmpty(text))
            return new CompressedBody(text is null ? null : [], 0, 0);

        var utf8Length = Encoding.UTF8.GetByteCount(text);
        var source = ArrayPool<byte>.Shared.Rent(utf8Length);
        var packed = ArrayPool<byte>.Shared.Rent(BrotliEncoder.GetMaxCompressedLength(utf8Length));
        try
        {
            Encoding.UTF8.GetBytes(text, source);
            if (!BrotliEncoder.TryCompress(
                    source.AsSpan(0, utf8Length), packed, out var written, Quality, Window))
                throw new InvalidOperationException("Brotli rejected a max-length destination.");

            return new CompressedBody(packed.AsSpan(0, written).ToArray(), utf8Length, text.Length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(packed);
            ArrayPool<byte>.Shared.Return(source);
        }
    }

    /// <summary>Inflates the whole body on every read. Call it once and reuse the result.</summary>
    public string? Text
    {
        get
        {
            if (_packed is null) return null;
            if (_packed.Length == 0) return "";

            var raw = ArrayPool<byte>.Shared.Rent(_utf8Length);
            try
            {
                if (!BrotliDecoder.TryDecompress(_packed, raw, out var written))
                    throw new InvalidOperationException("Brotli rejected an exactly sized destination.");

                return Encoding.UTF8.GetString(raw, 0, written);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(raw);
            }
        }
    }
}
