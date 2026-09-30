using System.Text.Json.Serialization;
using CodingAgentExplorer.Services;

namespace CodingAgentExplorer.Models;

public class ProxiedRequest
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    // Request
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    public Dictionary<string, string> RequestHeaders { get; set; } = [];

    // Bodies are held compressed and inflated on read, so the dashboard payload is byte for
    // byte what it always was while the store holds a fraction of the memory. Read the
    // properties sparingly: every get inflates the whole body. For size and emptiness checks
    // use RequestBodyChars / ResponseBodyChars, which are free.
    private CompressedBody _requestBody;

    public string? RequestBody
    {
        get => _requestBody.Text;
        set => _requestBody = CompressedBody.From(value);
    }

    [JsonIgnore]
    public int RequestBodyChars => _requestBody.Chars;

    // Parsed request fields
    public string? Model { get; set; }
    public bool IsStreaming { get; set; }
    public int? MaxTokens { get; set; }

    // Response
    public int? StatusCode { get; set; }
    public Dictionary<string, string> ResponseHeaders { get; set; } = [];

    private CompressedBody _responseBody;

    public string? ResponseBody
    {
        get => _responseBody.Text;
        set => _responseBody = CompressedBody.From(value);
    }

    [JsonIgnore]
    public int ResponseBodyChars => _responseBody.Chars;

    // Parsed response fields
    public string? MessageId { get; set; }
    public string? StopReason { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int? CacheCreationInputTokens { get; set; }
    public int? CacheReadInputTokens { get; set; }

    // SSE events (for streaming)
    public List<SseEvent> SseEvents { get; set; } = [];

    // Timing
    public double? DurationMs { get; set; }
    public double? TimeToFirstTokenMs { get; set; }

    // Error (proxy-level, not HTTP status)
    public string? Error { get; set; }
}
