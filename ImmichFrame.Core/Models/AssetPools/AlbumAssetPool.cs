using ImmichFrame.Core.Api;
using ImmichFrame.Core.Interfaces; // For IServerSettings
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ImmichFrame.Core.Models.AssetPools
{
    public class AlbumAssetPool : AssetPoolBase
    {
        private readonly Guid _albumId;
        public override string PoolName => $"Album_{_albumId}";

        public AlbumAssetPool(Guid albumId, IServerSettings settings, ImmichApi immichApi, ILogger<AlbumAssetPool> logger)
            : base(settings, immichApi, logger)
        {
            _albumId = albumId;
        }

        protected override async Task<int> FetchCountAsyncInternal()
        {
            _logger.LogDebug($"AlbumAssetPool ({_albumId}): Fetching total count.");
            try
            {
                var albumInfo = await _immichApi.GetAlbumInfoAsync(_albumId, null, null);
                if (albumInfo == null || albumInfo.Assets == null) return 0;

                var assetsList = albumInfo.Assets.ToList(); // Ensure it's a list for FetchMissingExifInfoAsync
                var assetsWithExif = await FetchMissingExifInfoAsync(assetsList);
                var filteredAssets = ApplyCommonFilters(assetsWithExif);
                return filteredAssets.Count();
            }
            catch (ApiException ex)
            {
                _logger.LogError(ex, $"AlbumAssetPool ({_albumId}): Error fetching album info for count.");
                return 0;
            }
        }

        protected override async Task<IEnumerable<AssetResponseDto>> FetchRandomAssetsAsync(int count)
        {
            if (count <= 0) return Enumerable.Empty<AssetResponseDto>();
            _logger.LogDebug($"AlbumAssetPool ({_albumId}): Fetching {count} random assets.");

            var searchDto = new MetadataSearchDto
            {
                AlbumId = _albumId,
                Type = AssetTypeEnum.IMAGE,
                Size = count
            };
            searchDto.Visibility = _settings.ShowArchived ? AssetVisibility.Archive : AssetVisibility.Timeline;
            searchDto.TakenAfter = _settings.ImagesFromDate ?? (_settings.ImagesFromDays.HasValue ? DateTime.Today.AddDays(-_settings.ImagesFromDays.Value) : null);
            searchDto.TakenBefore = _settings.ImagesUntilDate;
            if (_settings.Rating.HasValue) searchDto.Rating = _settings.Rating.Value;

            try
            {
                var result = await _immichApi.SearchAssetsAsync(searchDto);
                return result.Assets.Items ?? Enumerable.Empty<AssetResponseDto>();
            }
            catch (ApiException ex)
            {
                _logger.LogError(ex, $"AlbumAssetPool ({_albumId}): Error fetching random assets. If this fails due to AlbumId filter with random search, a fallback to client-side random selection might be needed.");
                return Enumerable.Empty<AssetResponseDto>();
            }
        }
    }
}
