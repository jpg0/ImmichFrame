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
public class AlbumAssetsPoolTests
{
    private Mock<ApiCache> _mockApiCache;
    private Mock<ImmichApi> _mockImmichApi;
    private Mock<IAccountSettings> _mockAccountSettings;
    private TestableAlbumAssetsPool _albumAssetsPool;

    private class TestableAlbumAssetsPool : AlbumAssetsPool
    {
        public TestableAlbumAssetsPool(ApiCache apiCache, ImmichApi immichApi, IAccountSettings accountSettings)
            : base(apiCache, immichApi, accountSettings) { }

        // Expose LoadAssets for testing
        public Task<IEnumerable<AssetResponseDto>> TestLoadAssets(CancellationToken ct = default)
        {
            return base.LoadAssets(ct);
        }
    }

    [SetUp]
    public void Setup()
    {
        _mockApiCache = new Mock<ApiCache>(null, null);
        _mockImmichApi = new Mock<ImmichApi>(null, null, null);
        _mockAccountSettings = new Mock<IAccountSettings>();
        _albumAssetsPool = new TestableAlbumAssetsPool(_mockApiCache.Object, _mockImmichApi.Object, _mockAccountSettings.Object);

        _mockAccountSettings.SetupGet(s => s.Albums).Returns(new List<Guid>());
        _mockAccountSettings.SetupGet(s => s.ExcludedAlbums).Returns(new List<Guid>());
    }

    private AssetResponseDto CreateAsset(string id) => new AssetResponseDto { Id = id, Type = AssetTypeEnum.IMAGE };

    [Test]
    public async Task LoadAssets_ReturnsAssetsPresentInBothIncludedAndExcludedAlbums_AsPerCurrentLogic()
    {
        // Arrange
        var album1Id = Guid.NewGuid();
        var excludedAlbumId = Guid.NewGuid();

        var assetA = CreateAsset("A"); // In album1
        var assetB = CreateAsset("B"); // In album1 and excludedAlbum
        var assetC = CreateAsset("C"); // In excludedAlbum only
        var assetD = CreateAsset("D"); // In album1 only (but not B)

        _mockAccountSettings.SetupGet(s => s.Albums).Returns(new List<Guid> { album1Id });
        _mockAccountSettings.SetupGet(s => s.ExcludedAlbums).Returns(new List<Guid> { excludedAlbumId });

        _mockImmichApi.Setup(api => api.GetAlbumInfoAsync(album1Id, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AlbumResponseDto { Assets = new List<AssetResponseDto> { assetA, assetB, assetD } });
        _mockImmichApi.Setup(api => api.GetAlbumInfoAsync(excludedAlbumId, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AlbumResponseDto { Assets = new List<AssetResponseDto> { assetB, assetC } });

        // Act
        var result = (await _albumAssetsPool.TestLoadAssets()).ToList();

        // Assert
        // Current logic: asset => excludedAlbumAssets.Any(exc => exc.Id == asset.Id)
        // This means it returns assets from 'Albums' that are ALSO in 'ExcludedAlbums'.
        Assert.AreEqual(1, result.Count);
        Assert.IsTrue(result.Any(a => a.Id == "B"));
        _mockImmichApi.Verify(api => api.GetAlbumInfoAsync(album1Id, null, null, It.IsAny<CancellationToken>()), Times.Once);
        _mockImmichApi.Verify(api => api.GetAlbumInfoAsync(excludedAlbumId, null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task LoadAssets_NoIncludedAlbums_ReturnsEmpty()
    {
        _mockAccountSettings.SetupGet(s => s.Albums).Returns(new List<Guid>());
        _mockAccountSettings.SetupGet(s => s.ExcludedAlbums).Returns(new List<Guid> { Guid.NewGuid() });
        _mockImmichApi.Setup(api => api.GetAlbumInfoAsync(It.IsAny<Guid>(), null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AlbumResponseDto { Assets = new List<AssetResponseDto> { CreateAsset("excluded_only") } });


        var result = (await _albumAssetsPool.TestLoadAssets()).ToList();
        Assert.IsEmpty(result);
    }

    [Test]
    public async Task LoadAssets_NoExcludedAlbums_ReturnsEmpty_AsPerCurrentLogic()
    {
        // With current logic, if ExcludedAlbums is empty, the .Any() check will always be false for the filter.
        var album1Id = Guid.NewGuid();
        _mockAccountSettings.SetupGet(s => s.Albums).Returns(new List<Guid> { album1Id });
        _mockAccountSettings.SetupGet(s => s.ExcludedAlbums).Returns(new List<Guid>()); // Empty excluded

        _mockImmichApi.Setup(api => api.GetAlbumInfoAsync(album1Id, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AlbumResponseDto { Assets = new List<AssetResponseDto> { CreateAsset("A") } });

        var result = (await _albumAssetsPool.TestLoadAssets()).ToList();
        Assert.IsEmpty(result, "Current logic requires item to be in excluded list, so empty excluded list means empty result.");
    }
}
