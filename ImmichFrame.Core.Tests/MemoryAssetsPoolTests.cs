using NUnit.Framework;
using Moq;
using ImmichFrame.Core.Api;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.Core.Logic.Pool;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;

namespace ImmichFrame.Core.Tests.Logic.Pool;

[TestFixture]
public class MemoryAssetsPoolTests
{
    private Mock<ApiCache> _mockApiCache;
    private Mock<ImmichApi> _mockImmichApi;
    private Mock<IAccountSettings> _mockAccountSettings;
    private MemoryAssetsPool _memoryAssetsPool;

    [SetUp]
    public void Setup()
    {
        _mockApiCache = new Mock<ApiCache>(null, null); // Base constructor requires ILogger and IOptions, pass null for simplicity in mock
        _mockImmichApi = new Mock<ImmichApi>(null, null, null); // Base constructor requires ILogger, IHttpClientFactory, IOptions, pass null
        _mockAccountSettings = new Mock<IAccountSettings>();

        _memoryAssetsPool = new MemoryAssetsPool(_mockApiCache.Object, _mockImmichApi.Object, _mockAccountSettings.Object);
    }

    private List<AssetResponseDto> CreateSampleAssets(int count, bool withExif, int yearCreated)
    {
        var assets = new List<AssetResponseDto>();
        for (int i = 0; i < count; i++)
        {
            var asset = new AssetResponseDto
            {
                Id = Guid.NewGuid().ToString(),
                OriginalPath = $"/path/to/image{i}.jpg",
                Type = AssetType.IMAGE,
                ExifInfo = withExif ? new ExifResponseDto { DateTimeOriginal = new DateTime(yearCreated, 1, 1) } : null,
                People = new List<PersonResponseDto>()
            };
            assets.Add(asset);
        }
        return assets;
    }

    private List<MemoryLaneResponseDto> CreateSampleMemories(int memoryCount, int assetsPerMemory, bool withExifInAssets, int memoryYear)
    {
        var memories = new List<MemoryLaneResponseDto>();
        for (int i = 0; i < memoryCount; i++)
        {
            var memory = new MemoryLaneResponseDto
            {
                Title = $"Memory {i}",
                Assets = CreateSampleAssets(assetsPerMemory, withExifInAssets, memoryYear),
                Data = new MemoryMetadataDto { Year = memoryYear }
            };
            memories.Add(memory);
        }
        return memories;
    }

    [Test]
    public async Task LoadAssets_CallsSearchMemoriesAsync()
    {
        // Arrange
        _mockImmichApi.Setup(x => x.SearchMemoriesAsync(It.IsAny<DateTime>(), null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryLaneResponseDto>());

        // Act
        // Access protected method via reflection for testing, or make it internal/public if design allows
        // For now, we assume LoadAssets is implicitly called by a public method of CachingApiAssetsPool (e.g. GetAsset)
        // Let's simulate this by calling a method that would trigger LoadAssets if cache is empty.
        // Since LoadAssets is protected, we'll test its effects via GetAsset.
        // We need to ensure the cache is empty or expired for LoadAssets to be called.
        _mockApiCache.Setup(c => c.GetFromCacheAsync<IEnumerable<AssetResponseDto>>(It.IsAny<string>(), It.IsAny<Func<Task<IEnumerable<AssetResponseDto>>>>()))
            .Returns<string, Func<Task<IEnumerable<AssetResponseDto>>>>(async (key, factory) => await factory());


        await _memoryAssetsPool.GetAsset(CancellationToken.None); // This should trigger LoadAssets

        // Assert
        _mockImmichApi.Verify(x => x.SearchMemoriesAsync(It.IsAny<DateTime>(), null, null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task LoadAssets_FetchesAssetInfo_WhenExifInfoIsNull()
    {
        // Arrange
        var memoryYear = DateTime.Now.Year - 2;
        var memories = CreateSampleMemories(1, 1, false, memoryYear); // Asset without ExifInfo
        var assetId = memories[0].Assets[0].Id;

        _mockImmichApi.Setup(x => x.SearchMemoriesAsync(It.IsAny<DateTime>(), null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(memories);
        _mockImmichApi.Setup(x => x.GetAssetInfoAsync(new Guid(assetId), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssetResponseDto { Id = assetId, ExifInfo = new ExifResponseDto { DateTimeOriginal = new DateTime(memoryYear, 1, 1) }, People = new List<PersonResponseDto>() });

        _mockApiCache.Setup(c => c.GetFromCacheAsync<IEnumerable<AssetResponseDto>>(It.IsAny<string>(), It.IsAny<Func<Task<IEnumerable<AssetResponseDto>>>>()))
            .Returns<string, Func<Task<IEnumerable<AssetResponseDto>>>>(async (key, factory) => await factory());

        // Act
        var resultAsset = await _memoryAssetsPool.GetAsset(CancellationToken.None); // Triggers LoadAssets

        // Assert
        _mockImmichApi.Verify(x => x.GetAssetInfoAsync(new Guid(assetId), null, It.IsAny<CancellationToken>()), Times.Once);
        Assert.IsNotNull(resultAsset.ExifInfo);
        Assert.AreEqual("2 years ago", resultAsset.ExifInfo.Description);
    }

    [Test]
    public async Task LoadAssets_DoesNotFetchAssetInfo_WhenExifInfoIsPresent()
    {
        // Arrange
        var memoryYear = DateTime.Now.Year - 1;
        var memories = CreateSampleMemories(1, 1, true, memoryYear); // Asset with ExifInfo
        var assetId = memories[0].Assets[0].Id;

        _mockImmichApi.Setup(x => x.SearchMemoriesAsync(It.IsAny<DateTime>(), null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(memories);

        _mockApiCache.Setup(c => c.GetFromCacheAsync<IEnumerable<AssetResponseDto>>(It.IsAny<string>(), It.IsAny<Func<Task<IEnumerable<AssetResponseDto>>>>()))
            .Returns<string, Func<Task<IEnumerable<AssetResponseDto>>>>(async (key, factory) => await factory());

        // Act
        var resultAsset = await _memoryAssetsPool.GetAsset(CancellationToken.None); // Triggers LoadAssets

        // Assert
        _mockImmichApi.Verify(x => x.GetAssetInfoAsync(It.IsAny<Guid>(), null, It.IsAny<CancellationToken>()), Times.Never);
        Assert.IsNotNull(resultAsset.ExifInfo);
        Assert.AreEqual("1 year ago", resultAsset.ExifInfo.Description);
    }

    [Test]
    public async Task LoadAssets_CorrectlyFormatsDescription_YearsAgo()
    {
        // Arrange
        var currentYear = DateTime.Now.Year;
        var testCases = new[]
        {
            new { year = currentYear - 1, expectedDesc = "1 year ago" },
            new { year = currentYear - 5, expectedDesc = "5 years ago" },
            new { year = currentYear, expectedDesc = "0 years ago" } // Or "This year" depending on desired logic, current is "0 years ago"
        };

        foreach (var tc in testCases)
        {
            var memories = CreateSampleMemories(1, 1, true, tc.year);
            memories[0].Assets[0].ExifInfo.DateTimeOriginal = new DateTime(tc.year, 1, 1); // Ensure Exif has the year

            _mockImmichApi.Setup(x => x.SearchMemoriesAsync(It.IsAny<DateTime>(), null, null, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(memories);

            // Reset and re-setup cache mock for each iteration to ensure factory is called
            _mockApiCache = new Mock<ApiCache>(null, null);
            _mockApiCache.Setup(c => c.GetFromCacheAsync<IEnumerable<AssetResponseDto>>(It.IsAny<string>(), It.IsAny<Func<Task<IEnumerable<AssetResponseDto>>>>()))
                         .Returns<string, Func<Task<IEnumerable<AssetResponseDto>>>>(async (key, factory) => await factory());
            _memoryAssetsPool = new MemoryAssetsPool(_mockApiCache.Object, _mockImmichApi.Object, _mockAccountSettings.Object);


            // Act
            var resultAsset = await _memoryAssetsPool.GetAsset(CancellationToken.None); // Triggers LoadAssets

            // Assert
            Assert.IsNotNull(resultAsset.ExifInfo);
            Assert.AreEqual(tc.expectedDesc, resultAsset.ExifInfo.Description, $"Failed for year {tc.year}");
        }
    }

    [Test]
    public async Task LoadAssets_AggregatesAssetsFromMultipleMemories()
    {
        // Arrange
        var memoryYear = DateTime.Now.Year - 3;
        var memories = CreateSampleMemories(2, 2, true, memoryYear); // 2 memories, 2 assets each

        _mockImmichApi.Setup(x => x.SearchMemoriesAsync(It.IsAny<DateTime>(), null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(memories);

        _mockApiCache.Setup(c => c.GetFromCacheAsync<IEnumerable<AssetResponseDto>>(It.IsAny<string>(), It.IsAny<Func<Task<IEnumerable<AssetResponseDto>>>>()))
            .Returns<string, Func<Task<IEnumerable<AssetResponseDto>>>>(async (key, factory) => await factory());

        // Act
        // To test aggregation, we need to retrieve all assets.
        // The current CachingApiAssetsPool might only load one by one.
        // We'll call GetAsset multiple times, assuming it cycles through.
        // This part of the test might be more suitable for CachingApiAssetsPoolTests if LoadAssets is meant to load ALL.
        // For now, let's assume LoadAssets loads everything and CachingApiAssetsPool makes them available.
        // The test for "all assets are loaded" is tricky without inspecting the cache or pool's internal list directly.
        // The current IAssetPool interface (GetAsset, PrefetchNextAsset) doesn't directly expose "GetAllLoadedAssets".
        // We will rely on the fact that the factory in GetFromCacheAsync is called, and it returns the list.
        // The count can be indirectly verified if we could access the pool's internal list after LoadAssets.

        // Let's refine the test to ensure LoadAssets returns the correct number of assets.
        // We need a way to inspect the result of LoadAssets directly.
        // We can make LoadAssets internal and use InternalsVisibleTo, or use reflection.
        // Or, we can rely on the setup of GetFromCacheAsync to capture the factory's result.
        IEnumerable<AssetResponseDto> loadedAssets = null;
        _mockApiCache.Setup(c => c.GetFromCacheAsync<IEnumerable<AssetResponseDto>>(It.IsAny<string>(), It.IsAny<Func<Task<IEnumerable<AssetResponseDto>>>>()))
            .Returns<string, Func<Task<IEnumerable<AssetResponseDto>>>>(async (key, factory) =>
            {
                loadedAssets = await factory();
                return loadedAssets;
            });

        await _memoryAssetsPool.GetAsset(CancellationToken.None); // Trigger load

        // Assert
        Assert.IsNotNull(loadedAssets);
        Assert.AreEqual(4, loadedAssets.Count()); // 2 memories * 2 assets
    }
}
