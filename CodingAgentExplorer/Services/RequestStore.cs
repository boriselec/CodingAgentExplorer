using System.Collections.Concurrent;
using CodingAgentExplorer.Models;

namespace CodingAgentExplorer.Services;

public class RequestStore
{
    private const long MaxBytes = 100L * 1024 * 1024;

    // Flat per-entry cost covering headers, scalar fields and object overhead. It also keeps
    // body-less requests (e.g. GET /) counting toward the budget, so the queue stays bounded.
    private const long EntryOverheadBytes = 1024;

    private readonly ConcurrentQueue<ProxiedRequest> _requests = new();
    private readonly object _lock = new();
    private long _totalBytes;

    public void Add(ProxiedRequest request)
    {
        lock (_lock)
        {
            _requests.Enqueue(request);
            _totalBytes += EstimateBytes(request);

            while (_totalBytes > MaxBytes && _requests.TryDequeue(out var evicted))
            {
                _totalBytes -= EstimateBytes(evicted);
            }
        }
    }

    public List<ProxiedRequest> GetAll()
    {
        return _requests.ToList();
    }

    public ProxiedRequest? GetById(string id)
    {
        return _requests.FirstOrDefault(r => r.Id == id);
    }

    public void Clear()
    {
        lock (_lock)
        {
            _requests.Clear();
            _totalBytes = 0;
        }
    }

    public int Count => _requests.Count;

    // Bodies dominate retained size, so only strings are measured. .NET strings are UTF-16.
    // Sizes are stable by the time a request is stored: SSE streaming completes first.
    private static long EstimateBytes(ProxiedRequest request)
    {
        long chars = (request.RequestBody?.Length ?? 0) + (request.ResponseBody?.Length ?? 0);

        foreach (var sseEvent in request.SseEvents)
        {
            chars += (sseEvent.Data?.Length ?? 0) + (sseEvent.EventType?.Length ?? 0);
        }

        return chars * sizeof(char) + EntryOverheadBytes;
    }
}
