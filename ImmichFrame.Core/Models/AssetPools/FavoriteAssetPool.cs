using ImmichFrame.Core.Api;
using ImmichFrame.Core.Interfaces; // For IServerSettings
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ImmichFrame.Core.Models.AssetPools
{
    public class FavoriteAssetPool : AssetPoolBase
    {
        public override string PoolName => "Favorites";

        public FavoriteAssetPool(IServerSettings settings, ImmichApi immichApi, ILogger<FavoriteAssetPool> logger)
            : base(settings, immichApi, logger) { }

        protected override async Task<int> FetchCountAsyncInternal()
        {
            _logger.LogDebug($"FavoriteAssetPool: Fetching total count.");
            var searchDto = new MetadataSearchDto
            {
                IsFavorite = true,
                Type = AssetTypeEnum.IMAGE,
                Size = 1 // Only need total count
            };
            searchDto.Visibility = _settings.ShowArchived ? AssetVisibility.Archive : AssetVisibility.Timeline;
            searchDto.TakenAfter = _settings.ImagesFromDate ?? (_settings.ImagesFromDays.HasValue ? DateTime.Today.AddDays(-_settings.ImagesFromDays.Value) : null);
            searchDto.TakenBefore = _settings.ImagesUntilDate;
            if (_settings.Rating.HasValue) searchDto.Rating = _settings.Rating.Value;

            try
            {
                var result = await _immichApi.SearchAssetsAsync(searchDto);
                return result.Assets.Total;
            }
            catch (ApiException ex)
            {
                _logger.LogError(ex, "FavoriteAssetPool: Error fetching favorite asset count.");
                return 0;
            }
        }

        protected override async Task<IEnumerable<AssetResponseDto>> FetchRandomAssetsAsync(int count)
        {
            if (count <= 0) return Enumerable.Empty<AssetResponseDto>();
            _logger.LogDebug($"FavoriteAssetPool: Fetching {count} random assets.");
            var searchDto = new MetadataSearchDto
            {
                IsFavorite = true,
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
                _logger.LogError(ex, $"FavoriteAssetPool: Error fetching random favorite assets.");
                return Enumerable.Empty<AssetResponseDto>();
            }
        }
    }
}
