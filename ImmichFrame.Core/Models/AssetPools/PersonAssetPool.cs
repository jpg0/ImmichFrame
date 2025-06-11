using ImmichFrame.Core.Api;
using ImmichFrame.Core.Interfaces; // For IServerSettings
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ImmichFrame.Core.Models.AssetPools
{
    public class PersonAssetPool : AssetPoolBase
    {
        private readonly Guid _personId;
        public override string PoolName => $"Person_{_personId}";

        public PersonAssetPool(Guid personId, IServerSettings settings, ImmichApi immichApi, ILogger<PersonAssetPool> logger)
            : base(settings, immichApi, logger)
        {
            _personId = personId;
        }

        protected override async Task<int> FetchCountAsyncInternal()
        {
            _logger.LogDebug($"PersonAssetPool ({_personId}): Fetching total count.");
            var searchDto = new MetadataSearchDto
            {
                PersonIds = new[] { _personId },
                Type = AssetTypeEnum.IMAGE,
                Size = 1
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
                _logger.LogError(ex, $"PersonAssetPool ({_personId}): Error fetching asset count.");
                return 0;
            }
        }

        protected override async Task<IEnumerable<AssetResponseDto>> FetchRandomAssetsAsync(int count)
        {
            if (count <= 0) return Enumerable.Empty<AssetResponseDto>();
            _logger.LogDebug($"PersonAssetPool ({_personId}): Fetching {count} random assets.");
            var searchDto = new MetadataSearchDto
            {
                PersonIds = new[] { _personId },
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
                _logger.LogError(ex, $"PersonAssetPool ({_personId}): Error fetching random assets.");
                return Enumerable.Empty<AssetResponseDto>();
            }
        }
    }
}
