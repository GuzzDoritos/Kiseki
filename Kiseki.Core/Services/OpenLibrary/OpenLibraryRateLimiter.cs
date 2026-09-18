namespace Kiseki.Core.Services.OpenLibrary;

public sealed class OpenLibraryRateLimiter
{
    private readonly object _lock = new();
    private readonly Queue<DateTimeOffset> _requestTimestamps = new();
    private readonly int _maxRequests;
    private readonly TimeSpan _window;

    public OpenLibraryRateLimiter(int maxRequests = 100, TimeSpan? window = null)
    {
        _maxRequests = maxRequests > 0 ? maxRequests : 100;
        _window = window ?? TimeSpan.FromMinutes(5);
    }

    public bool TryAcquire()
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            while (_requestTimestamps.Count > 0 && now - _requestTimestamps.Peek() > _window)
            {
                _requestTimestamps.Dequeue();
            }

            if (_requestTimestamps.Count >= _maxRequests)
            {
                return false;
            }

            _requestTimestamps.Enqueue(now);
            return true;
        }
    }
}

