using ImmichFrame.Core.Api;
using ImmichFrame.Core.Exceptions;
using ImmichFrame.Core.Helpers;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.Core.Models.AssetPools;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks; // Ensure Task is available

public class OptimizedImmichFrameLogic : IImmichFrameLogic, IDisposable
{
    private readonly IServerSettings _settings;
    private readonly HttpClient _httpClient;
    private ImmichApi _immichApi; // Made non-readonly for constructor init
    private readonly ILogger<OptimizedImmichFrameLogic> _logger;
    private readonly ILoggerFactory _loggerFactory; // Added
    private readonly ApiCache<List<IAssetPool>> _assetPoolCache; // Changed type
    private Random _random = new Random();

    public OptimizedImmichFrameLogic(IServerSettings settings, ILogger<OptimizedImmichFrameLogic> logger, ILoggerFactory loggerFactory) // Added loggerFactory
    {
        _settings = settings;
        _logger = logger;
        _loggerFactory = loggerFactory; // Stored loggerFactory
        _httpClient = new HttpClient();
        _httpClient.UseApiKey(_settings.ApiKey);
        _immichApi = new ImmichApi(_settings.ImmichServerUrl, _httpClient); // Initialized _immichApi
        _assetPoolCache = new ApiCache<List<IAssetPool>>(TimeSpan.FromHours(_settings.RefreshAlbumPeopleInterval)); // Changed type
    }

    public void Dispose()
    {
        _assetPoolCache.Dispose();
        _httpClient.Dispose();
        // GC.SuppressFinalize(this); // Only add if class has a finalizer ~OptimizedImmichFrameLogic()
    }

    private async Task<List<IAssetPool>> InitializeAssetPoolsAsync()
    {
        var assetPools = new List<IAssetPool>();
        _logger.LogInformation("OptimizedImmichFrameLogic: Initializing asset pools...");

        if (_settings.ShowFavorites)
        {
            var favPool = new FavoriteAssetPool(_settings, _immichApi, _loggerFactory.CreateLogger<FavoriteAssetPool>());
            if (await favPool.GetAssetCountAsync() > 0) // Also triggers initial count fetch if not done
            {
                assetPools.Add(favPool);
                favPool.StartBackgroundRefillAsync(); // Ensure queue starts filling
                _logger.LogInformation($"OptimizedImmichFrameLogic: Added FavoriteAssetPool with {await favPool.GetAssetCountAsync()} potential assets. Initial queue refill started.");
            }
            else
            {
                 _logger.LogInformation($"OptimizedImmichFrameLogic: FavoriteAssetPool has no assets (count is 0), not adding.");
            }
        }

        if (_settings.Albums?.Any() ?? false)
        {
            var excludedAlbumGuids = new HashSet<Guid>(_settings.ExcludedAlbums ?? Enumerable.Empty<Guid>());
            foreach (var albumId in _settings.Albums)
            {
                if (excludedAlbumGuids.Contains(albumId))
                {
                    _logger.LogDebug($"OptimizedImmichFrameLogic: Skipping excluded album ID {albumId}");
                    continue;
                }
                var albumPool = new AlbumAssetPool(albumId, _settings, _immichApi, _loggerFactory.CreateLogger<AlbumAssetPool>());
                if (await albumPool.GetAssetCountAsync() > 0)
                {
                    assetPools.Add(albumPool);
                    albumPool.StartBackgroundRefillAsync();
                    _logger.LogInformation($"OptimizedImmichFrameLogic: Added AlbumAssetPool for ID {albumId} with {await albumPool.GetAssetCountAsync()} potential assets. Initial queue refill started.");
                }
                else
                {
                    _logger.LogInformation($"OptimizedImmichFrameLogic: AlbumAssetPool for ID {albumId} has no assets (count is 0), not adding.");
                }
            }
        }

        if (_settings.People?.Any() ?? false)
        {
            foreach (var personId in _settings.People)
            {
                var personPool = new PersonAssetPool(personId, _settings, _immichApi, _loggerFactory.CreateLogger<PersonAssetPool>());
                if (await personPool.GetAssetCountAsync() > 0)
                {
                    assetPools.Add(personPool);
                    personPool.StartBackgroundRefillAsync();
                    _logger.LogInformation($"OptimizedImmichFrameLogic: Added PersonAssetPool for ID {personId} with {await personPool.GetAssetCountAsync()} potential assets. Initial queue refill started.");
                }
                else
                {
                     _logger.LogInformation($"OptimizedImmichFrameLogic: PersonAssetPool for ID {personId} has no assets (count is 0), not adding.");
                }
            }
        }

        if (_settings.ShowMemories)
        {
            var memPool = new MemoryAssetPool(_settings, _immichApi, _loggerFactory.CreateLogger<MemoryAssetPool>());
            if (await memPool.GetAssetCountAsync() > 0)
            {
                assetPools.Add(memPool);
                memPool.StartBackgroundRefillAsync();
                // MemoryAssetPool's InitializeAsync was already verbose, GetAssetCountAsync and StartBackgroundRefillAsync cover the logging.
                _logger.LogInformation($"OptimizedImmichFrameLogic: Added MemoryAssetPool with {await memPool.GetAssetCountAsync()} potential assets. Initial queue refill started.");
            }
            else
            {
                _logger.LogInformation($"OptimizedImmichFrameLogic: MemoryAssetPool has no assets (count is 0), not adding.");
            }
        }

        _logger.LogInformation($"OptimizedImmichFrameLogic: Finished initializing asset pools. Total active pools with assets: {assetPools.Count}.");
        return assetPools;
    }

    public async Task<AssetResponseDto?> GetNextAsset()
    {
        _logger.LogInformation("OptimizedImmichFrameLogic: GetNextAsset called. Fetching/retrieving asset pools from cache.");
        var assetPools = await _assetPoolCache.GetOrAddAsync("AssetPools", InitializeAssetPoolsAsync);

        if (assetPools == null || !assetPools.Any())
        {
            _logger.LogWarning("OptimizedImmichFrameLogic: No active asset pools found after attempting to initialize. Ensure settings allow for some assets to be selected.");
            return null;
        }

        long totalAssets = 0;
        List<string> poolSummaries = new List<string>();
        foreach (var pool in assetPools)
        {
            var count = await pool.GetAssetCountAsync();
            totalAssets += count;
            poolSummaries.Add($"'{pool.PoolName}': {count} assets");
        }
        _logger.LogInformation($"OptimizedImmichFrameLogic: Pool counts: {string.Join("; ", poolSummaries)}");

        if (totalAssets == 0)
        {
            _logger.LogWarning("OptimizedImmichFrameLogic: Total assets across all active pools is 0. Cannot select an asset.");
            return null;
        }
        _logger.LogInformation($"OptimizedImmichFrameLogic: Total assets available for selection: {totalAssets} from {assetPools.Count} active pools.");

        long randomAssetIndex = (long)(_random.NextDouble() * totalAssets);
        _logger.LogDebug($"OptimizedImmichFrameLogic: Random asset index chosen: {randomAssetIndex} (from total {totalAssets})");

        AssetResponseDto? selectedAsset = null;
        foreach (var pool in assetPools)
        {
            var poolCount = await pool.GetAssetCountAsync();
            if (randomAssetIndex < poolCount)
            {
                _logger.LogInformation($"OptimizedImmichFrameLogic: Selected pool '{pool.PoolName}' for random asset (pool's asset share: {poolCount} of total {totalAssets})");
                // Get asset from the selected pool's queue
                selectedAsset = await pool.GetNextAssetFromQueueAsync();

                if (selectedAsset != null)
                {
                    _logger.LogInformation($"OptimizedImmichFrameLogic: Successfully fetched asset ID {selectedAsset.Id} (Name: {selectedAsset.OriginalFileName}) from pool '{pool.PoolName}'s queue.");
                }
                else
                {
                    _logger.LogWarning($"OptimizedImmichFrameLogic: Pool '{pool.PoolName}' returned null from its queue. This may indicate the pool is temporarily empty or exhausted.");
                }
                break;
            }
            randomAssetIndex -= poolCount;
        }

        if (selectedAsset == null && totalAssets > 0)
        {
            _logger.LogWarning("OptimizedImmichFrameLogic: Failed to select an asset, though total available assets was greater than zero. This might indicate an issue with the selection logic, an empty pool that wasn't filtered out, or all selected pools returned null from their queues.");
        }
        else if (selectedAsset == null && totalAssets == 0)
        {
             _logger.LogInformation("OptimizedImmichFrameLogic: No assets available to select (total assets is 0).");
        }
        return selectedAsset;
    }

    // Removed FetchAssetPoolDetailsAsync
    // Removed GetFavoritePoolDetailsAsync
    // Removed AddAlbumPoolDetailsAsync
    // Removed AddPeoplePoolDetailsAsync
    // Removed GetMemoryPoolDetailsAsync
    // Removed FetchAssetFromPoolAsync
    // Removed GetNthFavoriteAssetAsync
    // Removed GetNthAlbumAssetAsync
    // Removed GetNthPersonAssetAsync

    public Task<AssetResponseDto> GetAssetInfoById(Guid assetId)
    {
        return _immichApi.GetAssetInfoAsync(assetId, null);
    }

    public async Task<IEnumerable<AlbumResponseDto>> GetAlbumInfoById(Guid assetId)
    {
        return await _immichApi.GetAllAlbumsAsync(assetId, null);
    }

    readonly string DownloadLocation = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ImageCache");
    public async Task<(string fileName, string ContentType, Stream fileStream)> GetImage(Guid id)
    {
        // Check if the image is already downloaded
        if (_settings.DownloadImages)
        {
            if (!Directory.Exists(DownloadLocation))
            {
                Directory.CreateDirectory(DownloadLocation);
            }

            var file = Directory.GetFiles(DownloadLocation).FirstOrDefault(x => Path.GetFileNameWithoutExtension(x) == id.ToString());

            if (!string.IsNullOrWhiteSpace(file))
            {
                if (_settings.RenewImagesDuration > (DateTime.UtcNow - File.GetCreationTimeUtc(file)).Days)
                {
                    var fs = File.OpenRead(file);

                    var ex = Path.GetExtension(file);

                    return (Path.GetFileName(file), $"image/{ex}", fs);
                }

                File.Delete(file);
            }
        }

        var data = await _immichApi.ViewAssetAsync(id, string.Empty, AssetMediaSize.Preview);

        if (data == null)
            throw new AssetNotFoundException($"Asset {id} was not found!");

        var contentType = "";
        if (data.Headers.ContainsKey("Content-Type"))
        {
            contentType = data.Headers["Content-Type"].FirstOrDefault()?.ToString() ?? "";
        }
        var ext = contentType.ToLower() == "image/webp" ? "webp" : "jpeg";
        var fileName = $"{id}.{ext}";

        if (_settings.DownloadImages)
        {
            var stream = data.Stream;

            var filePath = Path.Combine(DownloadLocation, fileName);

            // save to folder
            var fs = File.Create(filePath);
            stream.CopyTo(fs);
            fs.Position = 0;
            return (Path.GetFileName(filePath), contentType, fs);
        }

        return (fileName, contentType, data.Stream);
    }

    public Task SendWebhookNotification(IWebhookNotification notification) => WebhookHelper.SendWebhookNotification(notification, _settings.Webhook);
}