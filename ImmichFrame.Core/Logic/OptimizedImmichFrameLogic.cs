using System.Threading.Channels;
using ImmichFrame.Core.Api;
using ImmichFrame.Core.Exceptions;
using ImmichFrame.Core.Helpers;
using ImmichFrame.Core.Interfaces;
using Microsoft.Extensions.Logging;

public class OptimizedImmichFrameLogic : IImmichFrameLogic, IDisposable
{
    private readonly IAccountSettings _accountSettings;
    private readonly IGeneralSettings _frameSettings;
    private readonly HttpClient _httpClient;
    private readonly ImmichApi _immichApi;
    private readonly ApiCache _apiCache;
    private readonly ILogger<OptimizedImmichFrameLogic> _logger;

    public OptimizedImmichFrameLogic(IAccountSettings accountSettings, IGeneralSettings frameSettings, ILogger<OptimizedImmichFrameLogic> logger)
    {
        _accountSettings = accountSettings;
        _frameSettings = frameSettings;
        _logger = logger;
        _httpClient = new HttpClient();
        _httpClient.UseApiKey(_accountSettings.ApiKey);
        _immichApi = new ImmichApi(_accountSettings.ImmichServerUrl, _httpClient);
        _apiCache = new ApiCache(TimeSpan.FromHours(_frameSettings.RefreshAlbumPeopleInterval));
    }

    public void Dispose()
    {
        _apiCache.Dispose();
        _httpClient.Dispose();
    }

    private Channel<AssetResponseDto> _assetQueue = Channel.CreateUnbounded<AssetResponseDto>();
    private readonly SemaphoreSlim _isReloadingAssets = new(1, 1);

    public async Task<AssetResponseDto?> GetNextAsset()
    {
        try
        {
            if (_assetQueue.Reader.Count < 10)
            {
                // Fire-and-forget, reloading assets in the background
                ReloadAssetsAsync();
            }

            return await _assetQueue.Reader.ReadAsync(new CancellationTokenSource(TimeSpan.FromMinutes(1)).Token);
        }
        catch (OperationCanceledException)
        {
            // This exception occurs if the CancellationTokenSource times out
            _logger.LogWarning("Read asset list timed out");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError($"An unexpected error occurred while reading assets: {ex.Message}");
            throw;
        }
    }

    private async Task ReloadAssetsAsync()
    {
        if (await _isReloadingAssets.WaitAsync(0))
        {
            try
            {
                _logger.LogDebug("Reloading assets");
                foreach (var asset in await GetAssets())
                {
                    await _assetQueue.Writer.WriteAsync(asset);
                }
            }
            finally
            {
                _isReloadingAssets.Release();
            }
        }
        else
        {
            _logger.LogDebug("Assets already being loaded; not attempting a concurrent reload");
        }
    }

    public Task<AssetResponseDto> GetAssetInfoById(Guid assetId)
    {
        return _immichApi.GetAssetInfoAsync(assetId, null);
    }

    public async Task<IEnumerable<AlbumResponseDto>> GetAlbumInfoById(Guid assetId)
    {
        return await _immichApi.GetAllAlbumsAsync(assetId, null);
    }

    private int _assetAmount = 250;
    private Random _random = new Random();

    public async Task<IEnumerable<AssetResponseDto>> GetAssets()
    {
        if (!_accountSettings.ShowFavorites && !_accountSettings.ShowMemories && !_accountSettings.Albums.Any() && !_accountSettings.People.Any())
        {
            return await GetRandomAssets();
        }

        IEnumerable<AssetResponseDto> assets = new List<AssetResponseDto>();

        if (_accountSettings.ShowFavorites)
            assets = assets.Concat(await GetFavoriteAssets());
        if (_accountSettings.ShowMemories)
            assets = assets.Concat(await GetMemoryAssets());
        if (_accountSettings.Albums.Any())
            assets = assets.Concat(await GetAlbumAssets());
        if (_accountSettings.People.Any())
            assets = assets.Concat(await GetPeopleAssets());

        // Display only Images
        assets = assets.Where(x => x.Type == AssetTypeEnum.IMAGE);

        if (!_accountSettings.ShowArchived)
            assets = assets.Where(x => x.IsArchived == false);

        var takenBefore = _accountSettings.ImagesUntilDate.HasValue ? _accountSettings.ImagesUntilDate : null;
        if (takenBefore.HasValue)
        {
            assets = assets.Where(x => x.ExifInfo.DateTimeOriginal <= takenBefore);
        }

        var takenAfter = _accountSettings.ImagesFromDate.HasValue ? _accountSettings.ImagesFromDate : _accountSettings.ImagesFromDays.HasValue ? DateTime.Today.AddDays(-_accountSettings.ImagesFromDays.Value) : null;
        if (takenAfter.HasValue)
        {
            assets = assets.Where(x => x.ExifInfo.DateTimeOriginal >= takenAfter);
        }

        if (_accountSettings.Rating is int rating)
        {
            assets = assets.Where(x => x.ExifInfo.Rating == rating);
        }

        if (_accountSettings.ExcludedAlbums.Any())
        {
            var excludedAssetList = await GetExcludedAlbumAssets();
            var excludedAssetSet = excludedAssetList.Select(x => x.Id).ToHashSet();
            assets = assets.Where(x => !excludedAssetSet.Contains(x.Id));
        }

        assets = assets.OrderBy(asset => _random.Next());

        var assetsList = assets.ToList();
        if (assetsList.Count > _assetAmount)
        {
            assetsList = assetsList.Take(_assetAmount).ToList();
        }

        return assetsList;
    }

    public async Task<IEnumerable<AssetResponseDto>> GetRandomAssets()
    {
        var searchDto = new RandomSearchDto
        {
            Size = _assetAmount,
            Type = AssetTypeEnum.IMAGE,
            WithExif = true,
            WithPeople = true
        };

        if (_accountSettings.ShowArchived)
        {
            searchDto.Visibility = AssetVisibility.Archive;
        }
        else
        {
            searchDto.Visibility = AssetVisibility.Timeline;
        }

        var takenBefore = _accountSettings.ImagesUntilDate.HasValue ? _accountSettings.ImagesUntilDate : null;
        if (takenBefore.HasValue)
        {
            searchDto.TakenBefore = takenBefore;
        }
        var takenAfter = _accountSettings.ImagesFromDate.HasValue ? _accountSettings.ImagesFromDate : _accountSettings.ImagesFromDays.HasValue ? DateTime.Today.AddDays(-_accountSettings.ImagesFromDays.Value) : null;

        if (takenAfter.HasValue)
        {
            searchDto.TakenAfter = takenAfter;
        }

        if (_accountSettings.Rating is int rating)
        {
            searchDto.Rating = rating;
        }

        var assets = await _immichApi.SearchRandomAsync(searchDto);

        if (_accountSettings.ExcludedAlbums.Any())
        {
            var excludedAssetList = await GetExcludedAlbumAssets();
            var excludedAssetSet = excludedAssetList.Select(x => x.Id).ToHashSet();
            assets = assets.Where(x => !excludedAssetSet.Contains(x.Id)).ToList();
        }

        return assets;
    }

    public async Task<IEnumerable<AssetResponseDto>> GetMemoryAssets()
    {
        return await _apiCache.GetOrAddAsync("MemoryAssets", async () =>
        {
            var today = DateTime.Today;
            var memories = await _immichApi.SearchMemoriesAsync(DateTime.Now, null, null, null);

            var memoryAssets = new List<AssetResponseDto>();
            foreach (var memory in memories)
            {
                var assets = memory.Assets.ToList();
                var yearsAgo = DateTime.Now.Year - memory.Data.Year;

                foreach (var asset in assets)
                {
                    if (asset.ExifInfo == null)
                    {
                        var assetInfo = await GetAssetInfoById(new Guid(asset.Id));
                        asset.ExifInfo = assetInfo.ExifInfo;
                        asset.People = assetInfo.People;
                    }
                    asset.ExifInfo.Description = $"{yearsAgo} {(yearsAgo == 1 ? "year" : "years")} ago";
                }

                memoryAssets.AddRange(assets);
            }

            return memoryAssets;
        });
    }

    public async Task<AssetStatsResponseDto> GetAssetStats()
    {
        return await _apiCache.GetOrAddAsync("AssetStats",
            () => _immichApi.GetAssetStatisticsAsync(null, false, null));
    }

    public async Task<IEnumerable<AssetResponseDto>> GetFavoriteAssets()
    {
        return await _apiCache.GetOrAddAsync("FavoriteAssets", async () =>
        {
            var favoriteAssets = new List<AssetResponseDto>();

            int page = 1;
            int batchSize = 1000;
            int total;
            do
            {
                var metadataBody = new MetadataSearchDto
                {
                    Page = page,
                    Size = batchSize,
                    IsFavorite = true,
                    Type = AssetTypeEnum.IMAGE,
                    WithExif = true,
                    WithPeople = true
                };

                var favoriteInfo = await _immichApi.SearchAssetsAsync(metadataBody);

                total = favoriteInfo.Assets.Total;

                favoriteAssets.AddRange(favoriteInfo.Assets.Items);
                page++;
            } while (total == batchSize);

            return favoriteAssets;
        });
    }

    public async Task<IEnumerable<AssetResponseDto>> GetAlbumAssets()
    {
        return await _apiCache.GetOrAddAsync("AlbumAssets", async () =>
        {
            var albumAssets = new List<AssetResponseDto>();

            foreach (var albumId in _accountSettings.Albums)
            {
                var albumInfo = await _immichApi.GetAlbumInfoAsync(albumId, null, null);

                albumAssets.AddRange(albumInfo.Assets);
            }

            return albumAssets;
        });
    }

    public async Task<IEnumerable<AssetResponseDto>> GetExcludedAlbumAssets()
    {
        return await _apiCache.GetOrAddAsync("ExcludedAlbumAssets", async () =>
        {
            var excludedAlbumAssets = new List<AssetResponseDto>();

            foreach (var albumId in _accountSettings.ExcludedAlbums)
            {
                var albumInfo = await _immichApi.GetAlbumInfoAsync(albumId, null, null);

                excludedAlbumAssets.AddRange(albumInfo.Assets);
            }

            return excludedAlbumAssets;
        });
    }

    public async Task<IEnumerable<AssetResponseDto>> GetPeopleAssets()
    {
        return await _apiCache.GetOrAddAsync("PeopleAssets", async () =>
        {
            var personAssets = new List<AssetResponseDto>();

            foreach (var personId in _accountSettings.People)
            {
                int page = 1;
                int batchSize = 1000;
                int total;
                do
                {
                    var metadataBody = new MetadataSearchDto
                    {
                        Page = page,
                        Size = batchSize,
                        PersonIds = new[] { personId },
                        Type = AssetTypeEnum.IMAGE,
                        WithExif = true,
                        WithPeople = true
                    };

                    var personInfo = await _immichApi.SearchAssetsAsync(metadataBody);

                    total = personInfo.Assets.Total;

                    personAssets.AddRange(personInfo.Assets.Items);
                    page++;
                } while (total == batchSize);
            }

            return personAssets;
        });
    }

    readonly string DownloadLocation = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ImageCache");

    public async Task<(string fileName, string ContentType, Stream fileStream)> GetImage(Guid id)
    {
        // Check if the image is already downloaded
        if (_frameSettings.DownloadImages)
        {
            if (!Directory.Exists(DownloadLocation))
            {
                Directory.CreateDirectory(DownloadLocation);
            }

            var file = Directory.GetFiles(DownloadLocation)
                .FirstOrDefault(x => Path.GetFileNameWithoutExtension(x) == id.ToString());

            if (!string.IsNullOrWhiteSpace(file))
            {
                if (_frameSettings.RenewImagesDuration > (DateTime.UtcNow - File.GetCreationTimeUtc(file)).Days)
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

        if (_frameSettings.DownloadImages)
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

    public Task SendWebhookNotification(IWebhookNotification notification) =>
        WebhookHelper.SendWebhookNotification(notification, _frameSettings.Webhook);
}