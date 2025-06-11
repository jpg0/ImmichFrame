using ImmichFrame.Core.Api;
using ImmichFrame.Core.Exceptions;
using ImmichFrame.Core.Helpers;
using ImmichFrame.Core.Interfaces;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.Collections.Generic;

public class AssetPoolDetails {
    public int Count { get; set; }
    public List<Guid>? MemoryAssetIds { get; set; } // Specific to memory pool
}

public class OptimizedImmichFrameLogic : IImmichFrameLogic, IDisposable
{
    private readonly IServerSettings _settings;
    private readonly HttpClient _httpClient;
    private readonly ImmichApi _immichApi;
    private readonly ILogger<OptimizedImmichFrameLogic> _logger;
    private readonly ApiCache<Dictionary<string, AssetPoolDetails>> _assetPoolCache;
    private Random _random = new Random();

    public OptimizedImmichFrameLogic(IServerSettings settings, ILogger<OptimizedImmichFrameLogic> logger)
    {
        _settings = settings;
        _logger = logger;
        _httpClient = new HttpClient();
        _httpClient.UseApiKey(_settings.ApiKey);
        _immichApi = new ImmichApi(_settings.ImmichServerUrl, _httpClient);
        _assetPoolCache = new ApiCache<Dictionary<string, AssetPoolDetails>>(TimeSpan.FromHours(_settings.RefreshAlbumPeopleInterval));
    }

    public void Dispose()
    {
        _assetPoolCache.Dispose();
        _httpClient.Dispose();
    }

    public async Task<AssetResponseDto?> GetNextAsset()
    {
        _logger.LogInformation("OptimizedImmichFrameLogic: GetNextAsset called. Fetching/retrieving asset pool details from cache.");
        var poolDetailsDictionary = await _assetPoolCache.GetOrAddAsync("AllAssetPoolDetails", FetchAssetPoolDetailsAsync);

        if (poolDetailsDictionary == null || !poolDetailsDictionary.Any(kvp => kvp.Value.Count > 0))
        {
            _logger.LogWarning("OptimizedImmichFrameLogic: No asset pool details found or all pools are empty after attempting to fetch.");
            return null;
        }

        // Calculate total assets available across all pools
        long totalAssets = 0;
        foreach (var poolDetail in poolDetailsDictionary.Values)
        {
            totalAssets += poolDetail.Count;
        }

        if (totalAssets == 0)
        {
            _logger.LogWarning("OptimizedImmichFrameLogic: Total assets across all pools is 0. Cannot select an asset.");
            return null;
        }

        _logger.LogInformation($"OptimizedImmichFrameLogic: Total assets available for selection: {totalAssets}");

        // Generate a random number to pick an asset across all pools
        long randomAssetIndex = (long)(_random.NextDouble() * totalAssets); // Use long for large counts

        _logger.LogDebug($"OptimizedImmichFrameLogic: Random asset index selected: {randomAssetIndex} (out of {totalAssets})");

        // Determine which pool the selected asset belongs to
        AssetResponseDto? selectedAsset = null;
        string selectedPoolKey = "unknown";

        foreach (var poolEntry in poolDetailsDictionary)
        {
            if (randomAssetIndex < poolEntry.Value.Count)
            {
                selectedPoolKey = poolEntry.Key;
                _logger.LogInformation($"OptimizedImmichFrameLogic: Selected pool '{selectedPoolKey}' with original index {randomAssetIndex} (pool count: {poolEntry.Value.Count})");
                selectedAsset = await FetchAssetFromPoolAsync(poolEntry.Key, (int)randomAssetIndex, poolEntry.Value);
                break;
            }
            randomAssetIndex -= poolEntry.Value.Count;
        }

        if (selectedAsset == null)
        {
            _logger.LogWarning("OptimizedImmichFrameLogic: Could not select an asset even though totalAssets > 0. This might indicate an issue in pool iteration or FetchAssetFromPoolAsync. Selected pool key was '{selectedPoolKey}'.", selectedPoolKey);
        }
        else
        {
             _logger.LogInformation($"OptimizedImmichFrameLogic: Successfully fetched asset ID {selectedAsset.Id} from pool '{selectedPoolKey}'.");
        }
        return selectedAsset;
    }

    private async Task<Dictionary<string, AssetPoolDetails>> FetchAssetPoolDetailsAsync()
    {
        var poolCollectionDetails = new Dictionary<string, AssetPoolDetails>();
        _logger.LogInformation("OptimizedImmichFrameLogic: Fetching asset pool details...");

        if (_settings.ShowFavorites)
        {
            try
            {
                var details = await GetFavoritePoolDetailsAsync();
                if (details.Count > 0) poolCollectionDetails["favorites"] = details;
                _logger.LogInformation($"OptimizedImmichFrameLogic: Favorites count: {details.Count}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OptimizedImmichFrameLogic: Error fetching favorite pool details");
            }
        }

        if (_settings.Albums?.Any() ?? false)
        {
            try
            {
                await AddAlbumPoolDetailsAsync(poolCollectionDetails);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OptimizedImmichFrameLogic: Error fetching album pool details");
            }
        }

        if (_settings.People?.Any() ?? false)
        {
            try
            {
                await AddPeoplePoolDetailsAsync(poolCollectionDetails);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OptimizedImmichFrameLogic: Error fetching people pool details");
            }
        }

        if (_settings.ShowMemories)
        {
            try
            {
                var details = await GetMemoryPoolDetailsAsync();
                if (details.Count > 0) poolCollectionDetails["memories"] = details;
                _logger.LogInformation($"OptimizedImmichFrameLogic: Memories count: {details.Count}, IDs collected: {details.MemoryAssetIds?.Count}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OptimizedImmichFrameLogic: Error fetching memory pool details");
            }
        }

        _logger.LogInformation("OptimizedImmichFrameLogic: Finished fetching asset pool details. Total pools with assets: {PoolCount}", poolCollectionDetails.Count(kvp => kvp.Value.Count > 0));
        return poolCollectionDetails;
    }

    private async Task<AssetPoolDetails> GetFavoritePoolDetailsAsync()
    {
        _logger.LogDebug("OptimizedImmichFrameLogic: Getting favorite pool details.");
        var searchDto = new MetadataSearchDto { IsFavorite = true, Type = AssetTypeEnum.IMAGE, Size = 1 };
        searchDto.Visibility = _settings.ShowArchived ? AssetVisibility.Archive : AssetVisibility.Timeline;
        searchDto.TakenAfter = _settings.ImagesFromDate ?? (_settings.ImagesFromDays.HasValue ? DateTime.Today.AddDays(-_settings.ImagesFromDays.Value) : null);
        searchDto.TakenBefore = _settings.ImagesUntilDate;
        if (_settings.Rating.HasValue) searchDto.Rating = _settings.Rating.Value;
        var result = await _immichApi.SearchAssetsAsync(searchDto);
        return new AssetPoolDetails { Count = result.Assets.Total };
    }

    private async Task AddAlbumPoolDetailsAsync(Dictionary<string, AssetPoolDetails> poolCollection)
    {
        _logger.LogDebug("OptimizedImmichFrameLogic: Getting album pool details for {AlbumCount} albums.", _settings.Albums.Count());
        var excludedAlbumGuids = new HashSet<Guid>(_settings.ExcludedAlbums ?? Enumerable.Empty<Guid>());
        var takenAfterDate = _settings.ImagesFromDate ?? (_settings.ImagesFromDays.HasValue ? DateTime.Today.AddDays(-_settings.ImagesFromDays.Value) : (DateTime?)null);
        var takenBeforeDate = _settings.ImagesUntilDate;

        foreach (var albumId in _settings.Albums)
        {
            if (excludedAlbumGuids.Contains(albumId))
            {
                _logger.LogDebug("OptimizedImmichFrameLogic: Skipping excluded album ID {AlbumId}", albumId);
                continue;
            }
            _logger.LogDebug("OptimizedImmichFrameLogic: Fetching details for album ID {AlbumId}", albumId);
            try
            {
                var albumInfo = await _immichApi.GetAlbumInfoAsync(albumId, null, null);
                IEnumerable<AssetResponseDto> assetsToFilter = albumInfo.Assets;
                if (!_settings.ShowArchived) assetsToFilter = assetsToFilter.Where(a => !a.IsArchived);
                assetsToFilter = assetsToFilter.Where(a => a.Type == AssetTypeEnum.IMAGE);
                if (takenAfterDate.HasValue) assetsToFilter = assetsToFilter.Where(a => a.ExifInfo?.DateTimeOriginal != null && a.LocalDateTime >= takenAfterDate.Value);
                if (takenBeforeDate.HasValue) assetsToFilter = assetsToFilter.Where(a => a.ExifInfo?.DateTimeOriginal != null && a.LocalDateTime <= takenBeforeDate.Value);
                if (_settings.Rating.HasValue) assetsToFilter = assetsToFilter.Where(a => a.ExifInfo?.Rating == _settings.Rating.Value);
                var count = assetsToFilter.Count();
                if (count > 0) poolCollection[$"album_{albumId}"] = new AssetPoolDetails { Count = count };
                _logger.LogInformation($"OptimizedImmichFrameLogic: Album ID {albumId} count: {count}");
            }
            catch (ApiException ex)
            {
                _logger.LogError(ex, "OptimizedImmichFrameLogic: Failed to get info for album {AlbumId}", albumId);
            }
        }
    }

    private async Task AddPeoplePoolDetailsAsync(Dictionary<string, AssetPoolDetails> poolCollection)
    {
        _logger.LogDebug("OptimizedImmichFrameLogic: Getting people pool details for {PeopleCount} people.", _settings.People.Count());
        foreach (var personId in _settings.People)
        {
            _logger.LogDebug("OptimizedImmichFrameLogic: Fetching details for person ID {PersonId}", personId);
            var searchDto = new MetadataSearchDto { PersonIds = new[] { personId }, Type = AssetTypeEnum.IMAGE, Size = 1 };
            searchDto.Visibility = _settings.ShowArchived ? AssetVisibility.Archive : AssetVisibility.Timeline;
            searchDto.TakenAfter = _settings.ImagesFromDate ?? (_settings.ImagesFromDays.HasValue ? DateTime.Today.AddDays(-_settings.ImagesFromDays.Value) : null);
            searchDto.TakenBefore = _settings.ImagesUntilDate;
            if (_settings.Rating.HasValue) searchDto.Rating = _settings.Rating.Value;
            try
            {
                var result = await _immichApi.SearchAssetsAsync(searchDto);
                if (result.Assets.Total > 0) poolCollection[$"person_{personId}"] = new AssetPoolDetails { Count = result.Assets.Total };
                _logger.LogInformation($"OptimizedImmichFrameLogic: Person ID {personId} count: {result.Assets.Total}");
            }
            catch (ApiException ex)
            {
                _logger.LogError(ex, "OptimizedImmichFrameLogic: Failed to search assets for person {PersonId}", personId);
            }
        }
    }

    private async Task<AssetPoolDetails> GetMemoryPoolDetailsAsync()
    {
        _logger.LogDebug("OptimizedImmichFrameLogic: Getting memory pool details.");
        var memories = await _immichApi.SearchMemoriesAsync(DateTime.Now, null, null, null);
        var allMemoryAssets = new List<AssetResponseDto>();
        foreach (var memory in memories) { allMemoryAssets.AddRange(memory.Assets); }
        _logger.LogDebug("OptimizedImmichFrameLogic: Total assets from all memories before filtering: {MemoryAssetCount}", allMemoryAssets.Count);

        var assetsToFetchExif = allMemoryAssets.Where(a => a.ExifInfo == null || a.ExifInfo.DateTimeOriginal == null).Select(a => a.Id).ToList();
        if(assetsToFetchExif.Any())
        {
           _logger.LogInformation("OptimizedImmichFrameLogic: Fetching missing ExifInfo for {AssetCount} memory assets.", assetsToFetchExif.Count);
           for(int i=0; i < allMemoryAssets.Count; i++)
           {
               if(assetsToFetchExif.Contains(allMemoryAssets[i].Id))
               {
                   try
                   {
                       _logger.LogDebug("OptimizedImmichFrameLogic: Fetching missing ExifInfo for memory asset ID {AssetId}", allMemoryAssets[i].Id);
                       allMemoryAssets[i] = await _immichApi.GetAssetInfoAsync(Guid.Parse(allMemoryAssets[i].Id), null);
                   }
                   catch(Exception ex)
                   {
                       _logger.LogWarning(ex, "OptimizedImmichFrameLogic: Failed to fetch ExifInfo for memory asset {AssetId}", allMemoryAssets[i].Id);
                   }
               }
           }
        }

        IEnumerable<AssetResponseDto> assetsToFilter = allMemoryAssets;
        if (!_settings.ShowArchived) assetsToFilter = assetsToFilter.Where(a => !a.IsArchived);
        assetsToFilter = assetsToFilter.Where(a => a.Type == AssetTypeEnum.IMAGE);
        var takenAfterDate = _settings.ImagesFromDate ?? (_settings.ImagesFromDays.HasValue ? DateTime.Today.AddDays(-_settings.ImagesFromDays.Value) : (DateTime?)null);
        var takenBeforeDate = _settings.ImagesUntilDate;
        if (takenAfterDate.HasValue) assetsToFilter = assetsToFilter.Where(a => a.ExifInfo?.DateTimeOriginal != null && a.LocalDateTime >= takenAfterDate.Value);
        if (takenBeforeDate.HasValue) assetsToFilter = assetsToFilter.Where(a => a.ExifInfo?.DateTimeOriginal != null && a.LocalDateTime <= takenBeforeDate.Value);
        if (_settings.Rating.HasValue) assetsToFilter = assetsToFilter.Where(a => a.ExifInfo?.Rating == _settings.Rating.Value);

        var filteredAssetsList = assetsToFilter.ToList();
        List<Guid> memoryAssetIds = filteredAssetsList.Select(a => Guid.Parse(a.Id)).Distinct().ToList();
        _logger.LogDebug("OptimizedImmichFrameLogic: Total memory assets after filtering: {FilteredMemoryAssetCount}", filteredAssetsList.Count);
        return new AssetPoolDetails { Count = memoryAssetIds.Count, MemoryAssetIds = memoryAssetIds };
    }

    public Task<AssetResponseDto> GetAssetInfoById(Guid assetId)
    {
        return _immichApi.GetAssetInfoAsync(assetId, null);
    }

    private async Task<AssetResponseDto?> FetchAssetFromPoolAsync(string poolKey, int indexInPool, AssetPoolDetails poolDetails)
    {
        _logger.LogDebug($"OptimizedImmichFrameLogic: Fetching asset from pool '{poolKey}' at index {indexInPool}.");
        string[] parts = poolKey.Split('_');
        string poolType = parts[0];
        Guid? entityId = parts.Length > 1 ? Guid.Parse(parts[1]) : (Guid?)null;

        try
        {
            switch (poolType)
            {
                case "favorites":
                    return await GetNthFavoriteAssetAsync(indexInPool);
                case "album":
                    if (entityId.HasValue) return await GetNthAlbumAssetAsync(entityId.Value, indexInPool);
                    _logger.LogWarning($"OptimizedImmichFrameLogic: Album ID missing for pool key '{poolKey}'."); return null;
                case "person":
                    if (entityId.HasValue) return await GetNthPersonAssetAsync(entityId.Value, indexInPool);
                    _logger.LogWarning($"OptimizedImmichFrameLogic: Person ID missing for pool key '{poolKey}'."); return null;
                case "memories":
                    if (poolDetails.MemoryAssetIds != null && indexInPool < poolDetails.MemoryAssetIds.Count)
                    {
                        var assetId = poolDetails.MemoryAssetIds[indexInPool];
                        _logger.LogDebug($"OptimizedImmichFrameLogic: Fetching memory asset by ID: {assetId}");
                        return await GetAssetInfoById(assetId); // Uses existing public method
                    }
                    _logger.LogWarning($"OptimizedImmichFrameLogic: Memory asset IDs not available or index out of bounds for pool '{poolKey}'. Index: {indexInPool}, Count: {poolDetails.MemoryAssetIds?.Count}");
                    return null;
                default:
                    _logger.LogWarning($"OptimizedImmichFrameLogic: Unknown pool type '{poolType}' from key '{poolKey}'.");
                    return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"OptimizedImmichFrameLogic: Error fetching asset from pool '{poolKey}' at index {indexInPool}.");
            return null;
        }
    }

    private async Task<AssetResponseDto?> GetNthFavoriteAssetAsync(int n)
    {
        _logger.LogDebug($"OptimizedImmichFrameLogic: Fetching {n}th favorite asset.");
        var searchDto = new MetadataSearchDto { IsFavorite = true, Type = AssetTypeEnum.IMAGE, Page = n + 1, Size = 1 }; // Page is 1-indexed
        searchDto.Visibility = _settings.ShowArchived ? AssetVisibility.Archive : AssetVisibility.Timeline;
        searchDto.TakenAfter = _settings.ImagesFromDate ?? (_settings.ImagesFromDays.HasValue ? DateTime.Today.AddDays(-_settings.ImagesFromDays.Value) : null);
        searchDto.TakenBefore = _settings.ImagesUntilDate;
        if (_settings.Rating.HasValue) searchDto.Rating = _settings.Rating.Value;
        var result = await _immichApi.SearchAssetsAsync(searchDto);
        return result.Assets.Items.FirstOrDefault();
    }

    private async Task<AssetResponseDto?> GetNthAlbumAssetAsync(Guid albumId, int n)
    {
        _logger.LogDebug($"OptimizedImmichFrameLogic: Fetching {n}th asset from album ID {albumId}.");
        var albumInfo = await _immichApi.GetAlbumInfoAsync(albumId, null, null);
        IEnumerable<AssetResponseDto> assetsToFilter = albumInfo.Assets;
        // Apply filters consistent with count calculation
        if (!_settings.ShowArchived) assetsToFilter = assetsToFilter.Where(a => !a.IsArchived);
        assetsToFilter = assetsToFilter.Where(a => a.Type == AssetTypeEnum.IMAGE);
        var takenAfterDate = _settings.ImagesFromDate ?? (_settings.ImagesFromDays.HasValue ? DateTime.Today.AddDays(-_settings.ImagesFromDays.Value) : (DateTime?)null);
        var takenBeforeDate = _settings.ImagesUntilDate;
        if (takenAfterDate.HasValue) assetsToFilter = assetsToFilter.Where(a => a.ExifInfo?.DateTimeOriginal != null && a.LocalDateTime >= takenAfterDate.Value);
        if (takenBeforeDate.HasValue) assetsToFilter = assetsToFilter.Where(a => a.ExifInfo?.DateTimeOriginal != null && a.LocalDateTime <= takenBeforeDate.Value);
        if (_settings.Rating.HasValue) assetsToFilter = assetsToFilter.Where(a => a.ExifInfo?.Rating == _settings.Rating.Value);
        var assetList = assetsToFilter.ToList();
        return (n < assetList.Count) ? assetList[n] : null;
    }

    private async Task<AssetResponseDto?> GetNthPersonAssetAsync(Guid personId, int n)
    {
        _logger.LogDebug($"OptimizedImmichFrameLogic: Fetching {n}th asset for person ID {personId}.");
        var searchDto = new MetadataSearchDto { PersonIds = new[] { personId }, Type = AssetTypeEnum.IMAGE, Page = n + 1, Size = 1 }; // Page is 1-indexed
        searchDto.Visibility = _settings.ShowArchived ? AssetVisibility.Archive : AssetVisibility.Timeline;
        searchDto.TakenAfter = _settings.ImagesFromDate ?? (_settings.ImagesFromDays.HasValue ? DateTime.Today.AddDays(-_settings.ImagesFromDays.Value) : null);
        searchDto.TakenBefore = _settings.ImagesUntilDate;
        if (_settings.Rating.HasValue) searchDto.Rating = _settings.Rating.Value;
        var result = await _immichApi.SearchAssetsAsync(searchDto);
        return result.Assets.Items.FirstOrDefault();
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