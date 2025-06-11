using ImmichFrame.Core.Api;
using ImmichFrame.Core.Interfaces;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent; // For ConcurrentQueue
using System.Collections.Generic;
using System.Linq;
using System.Threading; // For Interlocked
using System.Threading.Tasks;

namespace ImmichFrame.Core.Models.AssetPools
{
    public abstract class AssetPoolBase : IAssetPool
    {
        protected readonly IServerSettings _settings;
        protected readonly ImmichApi _immichApi;
        protected readonly ILogger _logger;

        protected ConcurrentQueue<AssetResponseDto> _assetQueue = new ConcurrentQueue<AssetResponseDto>();
        protected const int DesiredQueueLength = 10; // Hardcoded desired queue length
        protected const int RefillThreshold = 3;    // Hardcoded refill threshold

        private int _assetCount = -1; // -1 indicates count not yet fetched
        private object _countLock = new object();
        private object _refillLock = new object(); // Lock for synchronous part of refill
        private volatile bool _isRefilling = false; // To prevent concurrent background refills

        public abstract string PoolName { get; }

        protected AssetPoolBase(IServerSettings settings, ImmichApi immichApi, ILogger logger)
        {
            _settings = settings;
            _immichApi = immichApi;
            _logger = logger;

            // Eagerly fetch count and do initial fill in the background, but don't wait here.
            // This is a fire-and-forget task.
            _ = Task.Run(async () => {
                await GetAssetCountAsync(); // Ensure count is fetched
                await RefillQueueIfNeededAsync(isSynchronousCall: false, initialFill: true); // Perform initial fill
            });
        }

        public async Task<int> GetAssetCountAsync()
        {
            if (_assetCount < 0)
            {
                lock (_countLock)
                {
                    if (_assetCount < 0) // Double-check lock
                    {
                        // This is the first time, fetch the count
                        _assetCount = 0; // Default to 0 in case of error
                        try
                        {
                            _logger.LogInformation($"AssetPool '{PoolName}': First call to GetAssetCountAsync, fetching total asset count...");
                            var count = await FetchCountAsyncInternal();
                            _assetCount = count;
                            _logger.LogInformation($"AssetPool '{PoolName}': Total asset count is {_assetCount}.");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"AssetPool '{PoolName}': Error fetching total asset count.");
                            // _assetCount remains 0 or its last known value if an error occurs
                        }
                    }
                }
            }
            return _assetCount;
        }

        protected abstract Task<int> FetchCountAsyncInternal();

        public async Task<AssetResponseDto?> GetNextAssetFromQueueAsync()
        {
            // Non-blockingly trigger a background refill if needed.
            StartBackgroundRefillAsync();

            if (_assetQueue.TryDequeue(out var asset))
            {
                _logger.LogDebug($"AssetPool '{PoolName}': Dequeued asset. Queue size: {_assetQueue.Count}");
                return asset;
            }

            // Queue was empty, try a synchronous refill once.
            _logger.LogInformation($"AssetPool '{PoolName}': Queue empty. Attempting synchronous refill.");
            await RefillQueueIfNeededAsync(isSynchronousCall: true);

            if (_assetQueue.TryDequeue(out asset))
            {
                _logger.LogDebug($"AssetPool '{PoolName}': Dequeued asset after synchronous refill. Queue size: {_assetQueue.Count}");
                return asset;
            }

            _logger.LogWarning($"AssetPool '{PoolName}': Queue still empty after synchronous refill attempt. No asset returned. Total pool count: {_assetCount}. This might indicate the pool is exhausted or assets are being filtered out unexpectedly.");
            return null;
        }

        public void StartBackgroundRefillAsync()
        {
            // Check if a refill is already in progress, if so, don't start another.
            if (_isRefilling)
            {
                _logger.LogDebug($"AssetPool '{PoolName}': Background refill is already in progress.");
                return;
            }

            if (_assetQueue.Count <= RefillThreshold)
            {
                 _logger.LogInformation($"AssetPool '{PoolName}': Queue count ({_assetQueue.Count}) is at or below threshold ({RefillThreshold}). Starting background refill task.");
                // Fire-and-forget the refill task.
                _ = Task.Run(async () => await RefillQueueIfNeededAsync(isSynchronousCall: false));
            }
        }

        protected async Task RefillQueueIfNeededAsync(bool isSynchronousCall, bool initialFill = false)
        {
            // Use a simple flag to prevent multiple concurrent refill operations.
            // For synchronous calls, we might want to bypass this check or use a different lock.
            // However, the primary lock (_refillLock) below handles the critical section.
            if (!isSynchronousCall && _isRefilling) return;

            // Lock to ensure only one thread actively modifies the queue or fetches assets at a time,
            // especially for the part that decides if fetching is needed and then fetches.
            // The _isRefilling flag handles broader concurrency for background calls.
            lock(_refillLock)
            {
                if (_isRefilling && !isSynchronousCall) return; // Re-check after acquiring lock

                if (_assetQueue.Count >= DesiredQueueLength && !isSynchronousCall && !initialFill)
                {
                    _logger.LogDebug($"AssetPool '{PoolName}': Queue count ({_assetQueue.Count}) is sufficient. No refill needed now.");
                    return;
                }

                if (isSynchronousCall && _assetQueue.Count > 0 && !initialFill) // If sync call but queue not empty, don't refill unless initial.
                {
                     _logger.LogDebug($"AssetPool '{PoolName}': Synchronous call, but queue not empty. No refill.");
                     return;
                }

                // If we reach here, a refill is needed or forced.
                if (!isSynchronousCall)
                {
                    _isRefilling = true;
                }
            } // Release lock early after setting _isRefilling for background tasks or deciding to proceed for sync tasks.

            try
            {
                int assetsToFetch = DesiredQueueLength - _assetQueue.Count;
                if (assetsToFetch <= 0 && !isSynchronousCall && !initialFill) // Check again, queue might have been filled by another thread.
                {
                    if (!isSynchronousCall) _isRefilling = false;
                    return;
                }
                if (assetsToFetch <=0 && (isSynchronousCall || initialFill)) // If sync/initial and queue is full, nothing to do.
                {
                    if (!isSynchronousCall) _isRefilling = false;
                    return;
                }


                // Ensure we don't fetch more than the total available assets if the count is known and low.
                // This is a basic check; FetchRandomAssetsAsync should ideally handle not finding assets.
                if (_assetCount >= 0 && assetsToFetch > _assetCount && _assetCount > 0)
                {
                    // This condition is tricky. If _assetCount is the total unique assets, and queue is for non-unique randoms,
                    // then this cap might not be what we want. Assuming searchRandom can return duplicates if underlying pool is small.
                    // For now, let's fetch what's desired unless pool is smaller than desired fetch.
                     if (_assetCount < DesiredQueueLength) assetsToFetch = Math.Max(0, _assetCount - _assetQueue.Count);
                }

                if (assetsToFetch <= 0) { // No assets to fetch if pool is effectively smaller than queue or already full.
                    if (!isSynchronousCall) _isRefilling = false;
                    return;
                }


                _logger.LogInformation($"AssetPool '{PoolName}': {(isSynchronousCall ? "Synchronously r" : "R")}efilling queue. Current size: {_assetQueue.Count}, Desired: {DesiredQueueLength}. Fetching {assetsToFetch} assets.");

                IEnumerable<AssetResponseDto> newAssets = null;
                try
                {
                    newAssets = await FetchRandomAssetsAsync(assetsToFetch);
                }
                catch(Exception ex)
                {
                    _logger.LogError(ex, $"AssetPool '{PoolName}': Error during FetchRandomAssetsAsync.");
                     if (!isSynchronousCall) _isRefilling = false; // Reset flag on error
                    return;
                }

                if (newAssets != null)
                {
                    int addedCount = 0;
                    foreach (var asset in newAssets)
                    {
                        if (_assetQueue.Count < DesiredQueueLength) // Check again in case queue filled up rapidly
                        {
                            _assetQueue.Enqueue(asset);
                            addedCount++;
                        }
                        else
                        {
                            _logger.LogWarning($"AssetPool '{PoolName}': Queue reached desired length ({DesiredQueueLength}) during refill. {newAssets.Count() - addedCount} fetched assets not enqueued.");
                            break;
                        }
                    }
                    _logger.LogInformation($"AssetPool '{PoolName}': Added {addedCount} new assets to queue. New queue size: {_assetQueue.Count}.");
                }
                else
                {
                    _logger.LogWarning($"AssetPool '{PoolName}': FetchRandomAssetsAsync returned no assets.");
                }
            }
            catch(Exception ex)
            {
                 _logger.LogError(ex, $"AssetPool '{PoolName}': Exception during queue refill process.");
            }
            finally
            {
                if (!isSynchronousCall)
                {
                    _isRefilling = false; // Reset the flag for background refills
                }
            }
        }

        protected abstract Task<IEnumerable<AssetResponseDto>> FetchRandomAssetsAsync(int count);

        // Keep common filtering logic if needed by FetchCountAsyncInternal or if searchRandom doesn't cover all cases.
        // For now, assuming searchRandom handles all filters server-side.
        // If ApplyCommonFilters or FetchMissingExifInfoAsync are still needed for count estimation, they can be kept.
        // Based on user feedback, searchRandom has same filters, so these might not be needed for asset fetching.
        // They could still be relevant for GetAlbumInfo in AlbumAssetPool if that's used for count.
        protected IEnumerable<AssetResponseDto> ApplyCommonFilters(IEnumerable<AssetResponseDto> assets)
        {
            // This method might still be useful if FetchCountAsyncInternal for some pools (like Album)
            // gets all assets and then filters them to get a count.
            var filteredAssets = assets;
            if (!_settings.ShowArchived) filteredAssets = filteredAssets.Where(a => !a.IsArchived);
            filteredAssets = filteredAssets.Where(a => a.Type == AssetTypeEnum.IMAGE);
            var takenAfterDate = _settings.ImagesFromDate ?? (_settings.ImagesFromDays.HasValue ? DateTime.Today.AddDays(-_settings.ImagesFromDays.Value) : (DateTime?)null);
            if (takenAfterDate.HasValue) filteredAssets = filteredAssets.Where(a => a.ExifInfo?.DateTimeOriginal != null && a.LocalDateTime >= takenAfterDate.Value);
            var takenBeforeDate = _settings.ImagesUntilDate;
            if (takenBeforeDate.HasValue) filteredAssets = filteredAssets.Where(a => a.ExifInfo?.DateTimeOriginal != null && a.LocalDateTime <= takenBeforeDate.Value);
            if (_settings.Rating.HasValue) filteredAssets = filteredAssets.Where(a => a.ExifInfo?.Rating == _settings.Rating.Value);
            return filteredAssets;
        }

        protected async Task<List<AssetResponseDto>> FetchMissingExifInfoAsync(List<AssetResponseDto> assets)
        {
            // This might be needed if count estimation for some pools relies on EXIF data not initially present.
            var assetsToFetchExif = assets.Where(a => a.ExifInfo == null || a.ExifInfo.DateTimeOriginal == null).Select(a => a.Id).ToList();
            if (assetsToFetchExif.Any())
            {
                _logger.LogInformation($"AssetPool '{PoolName}': Fetching missing ExifInfo for {assetsToFetchExif.Count} assets (likely for count estimation).");
                for (int i = 0; i < assets.Count; i++)
                {
                    if (assetsToFetchExif.Contains(assets[i].Id))
                    {
                        try
                        {
                            var assetInfo = await _immichApi.GetAssetInfoAsync(Guid.Parse(assets[i].Id), null);
                            if (assetInfo != null) assets[i] = assetInfo;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, $"AssetPool '{PoolName}': Failed to fetch ExifInfo for asset {assets[i].Id}.");
                        }
                    }
                }
            }
            return assets;
        }
    }
}
