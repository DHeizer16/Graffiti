namespace GlobalGraffitiWall.API;

public class PaletteService
{
    private readonly CanvasRepository _repository;
    private readonly ILogger<PaletteService> _logger;
    private volatile bool[] _activeFlags = new bool[256];
    private volatile List<PaletteColor> _cachedPalette = new();
    private DateTime _lastCacheTime = DateTime.MinValue;
    private readonly TimeSpan _cacheTtl = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public PaletteService(CanvasRepository repository, ILogger<PaletteService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task InitializeAsync()
    {
        await RefreshCacheAsync(force: true);
    }

    public async Task<bool> IsColorActiveAsync(byte colorId)
    {
        if (DateTime.UtcNow - _lastCacheTime > _cacheTtl)
        {
            await RefreshCacheAsync(force: true);
        }
        return _activeFlags[colorId];
    }

    public bool IsColorActive(byte colorId)
    {
        return _activeFlags[colorId];
    }

    public async Task<IReadOnlyList<PaletteColor>> GetPaletteAsync(bool activeOnly = false)
    {
        if (DateTime.UtcNow - _lastCacheTime > _cacheTtl || _cachedPalette.Count == 0)
        {
            await RefreshCacheAsync(force: true);
        }

        return activeOnly
            ? _cachedPalette.Where(c => c.IsActive).OrderBy(c => c.SortOrder).ToList()
            : _cachedPalette.OrderBy(c => c.SortOrder).ToList();
    }

    public async Task RefreshCacheAsync(bool force = false)
    {
        if (!force && DateTime.UtcNow - _lastCacheTime < _cacheTtl) return;

        await _refreshLock.WaitAsync();
        try
        {
            if (!force && DateTime.UtcNow - _lastCacheTime < _cacheTtl) return;

            var colors = (await _repository.GetPaletteColorsAsync()).ToList();
            if (colors.Count > 0)
            {
                var flags = new bool[256];
                foreach (var c in colors)
                {
                    if (c.IsActive)
                    {
                        flags[c.Id] = true;
                    }
                }

                _activeFlags = flags;
                _cachedPalette = colors;
                _lastCacheTime = DateTime.UtcNow;
                _logger.LogInformation("Palette cache refreshed: {ActiveCount} active colors out of {TotalCount} total.",
                    colors.Count(c => c.IsActive), colors.Count);
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }
}
