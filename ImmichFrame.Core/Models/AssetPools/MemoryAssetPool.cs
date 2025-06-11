using ImmichFrame.Core.Api;
using ImmichFrame.Core.Interfaces; // For IServerSettings
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ImmichFrame.Core.Models.AssetPools
{
    public class MemoryAssetPool : AssetPoolBase
    {
        public override string PoolName => "Memories";

        public MemoryAssetPool(IServerSettings settings, ImmichApi immichApi, ILogger<MemoryAssetPool> logger)
            : base(settings, immichApi, logger) { }

        protected override async Task<int> FetchCountAsyncInternal()
        {
            _logger.LogDebug("MemoryAssetPool: Fetching total count of unique assets in memories matching filters.");
            try
            {
                var memories = await _immichApi.SearchMemoriesAsync(DateTime.Now, null, null, null);
                if (memories == null || !memories.Any()) return 0;

                var allMemoryAssets = new List<AssetResponseDto>();
                foreach (var memory in memories) { if(memory.Assets != null) allMemoryAssets.AddRange(memory.Assets); }

                if (!allMemoryAssets.Any()) return 0;

                var distinctAssetsList = allMemoryAssets.DistinctBy(a => a.Id).ToList();
                var assetsWithExif = await FetchMissingExifInfoAsync(distinctAssetsList);
                var filteredAssets = ApplyCommonFilters(assetsWithExif);
                return filteredAssets.Count();
            }
            catch (ApiException ex)
            {
                _logger.LogError(ex, "MemoryAssetPool: Error fetching memories for count.");
                return 0;
            }
        }

        protected override async Task<IEnumerable<AssetResponseDto>> FetchRandomAssetsAsync(int count)
        {
            if (count <= 0) return Enumerable.Empty<AssetResponseDto>();
            _logger.LogDebug($"MemoryAssetPool: Fetching {count} random assets using client-side random selection from filtered memory assets.");
            try
            {
                var memories = await _immichApi.SearchMemoriesAsync(DateTime.Now, null, null, null);
                if (memories == null || !memories.Any()) return Enumerable.Empty<AssetResponseDto>();

                var allMemoryAssets = new List<AssetResponseDto>();
                foreach (var memory in memories) { if(memory.Assets != null) allMemoryAssets.AddRange(memory.Assets); }

                if (!allMemoryAssets.Any()) return Enumerable.Empty<AssetResponseDto>();

                var distinctAssetsList = allMemoryAssets.DistinctBy(a => a.Id).ToList();
                var assetsWithExif = await FetchMissingExifInfoAsync(distinctAssetsList);
                var filteredAssets = ApplyCommonFilters(assetsWithExif).ToList();

                if (!filteredAssets.Any()) return Enumerable.Empty<AssetResponseDto>();

                var random = new Random();
                return filteredAssets.OrderBy(x => random.Next()).Take(count).ToList();
            }
            catch (ApiException ex)
            {
                _logger.LogError(ex, "MemoryAssetPool: Error fetching random assets from memories.");
                return Enumerable.Empty<AssetResponseDto>();
            }
        }
    }
}
