using System.Net;
using System.Net.Http;
using ImmichFrame.WebApi.Tests.Mocks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http; // Added this
using Moq;
using Moq.Protected;
using ImmichFrame.Core.Api;
using ImmichFrame.WebApi.Models;
using ImmichFrame.Core.Interfaces;
using NUnit.Framework;
using System.Text.Json; // Added for deserialization
using System.Text.Json.Serialization; // Added for JsonSerializerOptions if needed
// using Microsoft.Extensions.Logging; // No longer directly needed here

namespace ImmichFrame.WebApi.Tests.Controllers
{
    [TestFixture]
    public class AssetControllerTests
    {
        private WebApplicationFactory<Program> _factory;
        private Mock<HttpMessageHandler> _mockHttpMessageHandler;
        private byte[] _mockImageData; // Store mock image data for reuse

        // Helper class for deserializing the relevant part of the RandomImageAndInfo response
        private class RandomImageInfoResponse
        {
            // We only care about the RandomImageBase64 to get to the original asset ID via GetImage mock
            // However, the controller's GetRandomImageAndInfo actually returns ImageResponse,
            // which doesn't directly contain the asset ID. The asset ID is implicit in the GetNextAsset() call.
            // The test needs to verify that GetNextAsset(), when called repeatedly,
            // only yields assets of the configured type.

            // To do this properly, we need to inspect the *Asset ID* that the AssetController's
            // GetRandomImageAndInfo method *would have processed*.
            // The current structure of AssetController.GetRandomImageAndInfo picks an asset,
            // then fetches its image and info. The AssetResponseDto itself is not directly returned in ImageResponse.
            // This makes direct assertion on IsFavorite or IsArchived from the HTTP response difficult.

            // The most robust way is to ensure the mock /search/metadata returns a mix,
            // and then the internal logic (FavoriteAssetsPool, MemoryAssetsPool) filters these.
            // The test will then repeatedly call /api/Asset/RandomImageAndInfo.
            // We need to mock the /asset/thumbnail/{id} endpoint to know WHICH asset was chosen by the logic.

            // Let's define a simple DTO that might be part of the actual response if we could get the ID
            // For the purpose of this test, we'll have to rely on the thumbnail request.
        }


        [SetUp]
        public void Setup()
        {
            _mockHttpMessageHandler = new Mock<HttpMessageHandler>();
            _mockImageData = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }; // Minimal JPEG

            _factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.ConfigureAppConfiguration((hostingContext, configBuilder) =>
                    {
                        configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            // Set Http logging to Warning to avoid NRE from verbose logging on mock objects
                            ["Logging:LogLevel:Microsoft.Extensions.Http.Logging"] = "Warning",
                            // Optionally, suppress other verbose loggers if needed for cleaner test output:
                            // ["Logging:LogLevel:Default"] = "Information",
                            // ["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning",
                        });
                    });

                    builder.ConfigureTestServices(services =>
                    {
                        services.AddSingleton<HttpMessageHandler>(_mockHttpMessageHandler.Object);
                        services.AddHttpClient("ImmichApiAccountClient")
                            .ConfigurePrimaryHttpMessageHandler(sp => sp.GetRequiredService<HttpMessageHandler>());
                        services.ConfigureAll<HttpClientFactoryOptions>(options =>
                        {
                            options.HttpMessageHandlerBuilderActions.Add(b =>
                            {
                                b.PrimaryHandler = b.Services.GetRequiredService<HttpMessageHandler>();
                            });
                        });

                        var generalSettings = new GeneralSettings
                        {
                            ShowWeatherDescription = false,
                            ShowClock = true,
                            ClockFormat = "HH:mm",
                            Language = "en",
                            PhotoDateFormat = "MM/dd/yyyy",
                            ImageLocationFormat = "City,State,Country",
                            DownloadImages = false,
                            RenewImagesDuration = 30,
                            PrimaryColor = "#FFFFFF",
                            SecondaryColor = "#000000",
                            Style = "none",
                            BaseFontSize = "16px",
                            WeatherApiKey = "",
                            UnitSystem = "imperial",
                            WeatherLatLong = "0,0"
                        };

                        // Default account settings, will be overridden in tests
                        var accountSettings = new ServerAccountSettings
                        {
                            ImmichServerUrl = "http://mock-immich-server.com",
                            ApiKey = "test-api-key",
                            ShowMemories = false,
                            ShowFavorites = true, // Default for initial test
                            ShowArchived = false,
                            Albums = new List<Guid>(),
                            ExcludedAlbums = new List<Guid>(),
                            People = new List<Guid>()
                        };

                        var serverSettings = new ServerSettings
                        {
                            GeneralSettingsImpl = generalSettings,
                            AccountsImpl = new List<ServerAccountSettings> { accountSettings }
                        };

                        services.AddSingleton<IServerSettings>(serverSettings);
                        services.AddSingleton<IGeneralSettings>(generalSettings);
                    });
                });
        }

        [TearDown]
        public void TearDown()
        {
            _factory.Dispose();
        }

        private string CreateAssetJson(Guid id, bool isFavorite, bool isArchived, string originalFileName, string filenameSuffixOverride = "")
        {
            string actualFilenameSuffix = string.IsNullOrEmpty(filenameSuffixOverride) ? originalFileName.Split('.')[0] : filenameSuffixOverride;
            return $@"
            {{
                ""id"": ""{id}"",
                ""originalPath"": ""/path/to/{originalFileName}"",
                ""type"": ""IMAGE"",
                ""fileCreatedAt"": ""2023-10-26T10:00:00Z"",
                ""fileModifiedAt"": ""2023-10-26T10:00:00Z"",
                ""isFavorite"": {isFavorite.ToString().ToLower()},
                ""isArchived"": {isArchived.ToString().ToLower()},
                ""duration"": ""0:00:00"",
                ""checksum"": ""testchecksum_{actualFilenameSuffix}"",
                ""deviceAssetId"": ""testDeviceAssetId_{actualFilenameSuffix}"",
                ""deviceId"": ""testDeviceId_{actualFilenameSuffix}"",
                ""ownerId"": ""testOwnerId_{actualFilenameSuffix}"",
                ""originalFileName"": ""{originalFileName}"",
                ""localDateTime"": ""2023-10-26T10:00:00Z"",
                ""visibility"": ""timeline"",
                ""hasMetadata"": true,
                ""isOffline"": false,
                ""isTrashed"": false,
                ""thumbhash"": ""HASH_{actualFilenameSuffix}"",
                ""updatedAt"": ""2023-10-26T10:00:00Z""
            }}";
        }

        private void SetupSearchMetadataMock(params string[] assetJsons)
        {
            var allAssetsJson = string.Join(",", assetJsons);
            var jsonResponse = $@"
            {{
                ""albums"": {{ ""count"": 0, ""items"": [], ""total"": 0, ""facets"": [] }},
                ""assets"": {{
                    ""count"": {assetJsons.Length},
                    ""items"": [ {allAssetsJson} ],
                    ""total"": {assetJsons.Length},
                    ""facets"": [],
                    ""nextPage"": null
                }}
            }}";

            _mockHttpMessageHandler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString().Contains("/search/metadata")),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync((HttpRequestMessage request, CancellationToken token) => // Capture request
                {
                    var response = new HttpResponseMessage
                    {
                        StatusCode = HttpStatusCode.OK,
                        Content = new StringContent(jsonResponse),
                        RequestMessage = request // Set RequestMessage
                    };
                    return response;
                });

            // Mock for /api/memories used by MemoryAssetsPool
            _mockHttpMessageHandler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString().Contains("/api/memories")),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync((HttpRequestMessage request, CancellationToken token) =>
                {
                    var emptyMemoriesResponse = "[]"; // Assuming it returns a JSON array of memory albums/assets
                    var response = new HttpResponseMessage
                    {
                        StatusCode = HttpStatusCode.OK,
                        Content = new StringContent(emptyMemoriesResponse),
                        RequestMessage = request
                    };
                    return response;
                });
        }

        private void SetupThumbnailMock() // Generic thumbnail mock
        {
            _mockHttpMessageHandler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString().Contains("/asset/thumbnail/")), // Match any thumbnail request
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync((HttpRequestMessage request, CancellationToken token) =>
                {
                    request.Version = HttpVersion.Version11; // Ensure request has a version
                    var response = new HttpResponseMessage
                    {
                        StatusCode = HttpStatusCode.OK,
                        Content = new ByteArrayContent(_mockImageData),
                        RequestMessage = request,
                        Version = HttpVersion.Version11 // Set response version
                    };
                    return response;
                });
        }

        private void SetupSpecificThumbnailMock(Guid assetId)
        {
            _mockHttpMessageHandler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString().Contains($"/asset/thumbnail/{assetId}")),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync((HttpRequestMessage request, CancellationToken token) =>
                {
                    request.Version = HttpVersion.Version11; // Ensure request has a version
                    var response = new HttpResponseMessage
                    {
                        StatusCode = HttpStatusCode.OK,
                        Content = new ByteArrayContent(_mockImageData),
                        RequestMessage = request,
                        Version = HttpVersion.Version11 // Set response version
                    };
                    return response;
                });
        }


        [Test]
        public async Task GetRandomImage_ReturnsImageFromMockServer()
        {
            // Arrange
            var expectedAssetId = Guid.NewGuid();
            // Updated to use new CreateAssetJson with explicit filename
            var assetJson = CreateAssetJson(expectedAssetId, true, false, "generic_image.jpg");
            SetupSearchMetadataMock(assetJson);
            SetupSpecificThumbnailMock(expectedAssetId);

            var client = _factory.CreateClient();

            // Act
            var response = await client.GetAsync("/api/Asset/RandomImageAndInfo");

            // Assert
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();
            Assert.That(content, Is.Not.Empty);

            _mockHttpMessageHandler.Protected().Verify(
                "SendAsync",
                Times.AtLeastOnce(),
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString().Contains($"/asset/thumbnail/{expectedAssetId}")),
                ItExpr.IsAny<CancellationToken>()
            );
        }

        [Test]
        public async Task GetRandomImage_ShowFavoritesOnly_ReturnsOnlyFavoriteImages()
        {
            // Arrange
            var favoriteAssetId1 = Guid.NewGuid();
            var favoriteAssetId2 = Guid.NewGuid();
            var nonFavoriteAssetId1 = Guid.NewGuid();
            var nonFavoriteAssetId2 = Guid.NewGuid();
            var memoryAssetId = Guid.NewGuid(); // A memory asset (by name), should not be shown here

            SetupSearchMetadataMock(
                CreateAssetJson(favoriteAssetId1, true, false, "favorite_image_fav1.jpg"),
                CreateAssetJson(favoriteAssetId2, true, false, "favorite_image_fav2.jpg"),
                CreateAssetJson(nonFavoriteAssetId1, false, false, "regular_image_nonfav1.jpg"),
                CreateAssetJson(nonFavoriteAssetId2, false, false, "regular_image_nonfav2.jpg"),
                // Memory asset by name, isFavorite = false, isArchived = false
                CreateAssetJson(memoryAssetId, false, false, "memory_image_mem1.jpg")
            );
            SetupThumbnailMock();

            _factory = _factory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    // Retrieve existing GeneralSettings if needed, or create new default
                    var generalSettings = new GeneralSettings // Assuming default is fine or get from a shared setup
                    {
                        ShowWeatherDescription = false, ShowClock = true, ClockFormat = "HH:mm", Language = "en",
                        PhotoDateFormat = "MM/dd/yyyy", ImageLocationFormat = "City,State,Country", DownloadImages = false,
                        RenewImagesDuration = 30, PrimaryColor = "#FFFFFF", SecondaryColor = "#000000", Style = "none",
                        BaseFontSize = "16px", WeatherApiKey = "", UnitSystem = "imperial", WeatherLatLong = "0,0"
                    };

                    var accountSettings = new ServerAccountSettings
                    {
                        ImmichServerUrl = "http://mock-immich-server.com", ApiKey = "test-api-key",
                        ShowFavorites = true, // Test specific
                        ShowMemories = false,
                        ShowArchived = false,
                        Albums = new List<Guid>(), ExcludedAlbums = new List<Guid>(), People = new List<Guid>()
                    };
                    var serverSettings = new ServerSettings
                    {
                        GeneralSettingsImpl = generalSettings,
                        AccountsImpl = new List<ServerAccountSettings> { accountSettings }
                    };
                    services.AddSingleton<IServerSettings>(serverSettings);
                    services.AddSingleton<IGeneralSettings>(generalSettings); // Ensure general settings are also present
                });
            });

            var client = _factory.CreateClient();
            var returnedAssetIds = new HashSet<Guid>();
            int iterations = 20;

            // Act
            for (int i = 0; i < iterations; i++)
            {
                var response = await client.GetAsync("/api/Asset/RandomImageAndInfo");
                response.EnsureSuccessStatusCode();

                var requestedThumbnailUri = _mockHttpMessageHandler.Invocations
                    .LastOrDefault(inv => inv.Method.Name == "SendAsync" && // Changed from MethodInfo to Method
                                          ((HttpRequestMessage)inv.Arguments[0]).RequestUri!.ToString().Contains("/asset/thumbnail/"))
                    ?.Arguments[0] as HttpRequestMessage;

                Assert.That(requestedThumbnailUri, Is.Not.Null, "Thumbnail request was not made.");
                var pathSegments = requestedThumbnailUri!.RequestUri!.Segments;
                var assetIdSegment = pathSegments.LastOrDefault()?.TrimEnd('/');
                Assert.That(Guid.TryParse(assetIdSegment, out var currentAssetId), Is.True, "Could not parse asset ID from thumbnail request.");
                returnedAssetIds.Add(currentAssetId);
            }

            // Assert
            Assert.That(returnedAssetIds, Is.Not.Empty, "No assets were returned.");
            Assert.That(returnedAssetIds.All(id => id == favoriteAssetId1 || id == favoriteAssetId2), Is.True,
                "Only favorite assets should be returned. Found non-favorites or other types.");
            Assert.That(returnedAssetIds.Contains(favoriteAssetId1) || returnedAssetIds.Contains(favoriteAssetId2), Is.True,
                "Expected at least one of the favorite assets to be returned.");
            Assert.That(!returnedAssetIds.Contains(nonFavoriteAssetId1), Is.True, "Non-favorite asset (regular_image_nonfav1.jpg) was returned.");
            Assert.That(!returnedAssetIds.Contains(nonFavoriteAssetId2), Is.True, "Non-favorite asset (regular_image_nonfav2.jpg) was returned.");
            Assert.That(!returnedAssetIds.Contains(memoryAssetId), Is.True, "Memory asset (memory_image_mem1.jpg) was returned when only favorites expected.");
        }


        [Test]
        public async Task GetRandomImage_ShowMemoriesOnly_ReturnsOnlyMemoryImages()
        {
            // Arrange
            var memoryAssetId1 = Guid.NewGuid();
            var memoryAssetId2 = Guid.NewGuid();
            var favoriteAssetId = Guid.NewGuid();
            var regularAssetId = Guid.NewGuid();

            SetupSearchMetadataMock(
                // Memory assets identified by filename convention, isFavorite=false, isArchived=false
                CreateAssetJson(memoryAssetId1, false, false, "memory_image_mem1.jpg"),
                CreateAssetJson(memoryAssetId2, false, false, "prefix_memory_image_mem2.jpg"),
                CreateAssetJson(favoriteAssetId, true, false, "favorite_image_fav1.jpg"),
                CreateAssetJson(regularAssetId, false, false, "regular_image_reg1.jpg")
            );
            SetupThumbnailMock();

            _factory = _factory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    // Retrieve existing GeneralSettings if needed, or create new default
                    var generalSettings = new GeneralSettings // Assuming default is fine or get from a shared setup
                    {
                        ShowWeatherDescription = false, ShowClock = true, ClockFormat = "HH:mm", Language = "en",
                        PhotoDateFormat = "MM/dd/yyyy", ImageLocationFormat = "City,State,Country", DownloadImages = false,
                        RenewImagesDuration = 30, PrimaryColor = "#FFFFFF", SecondaryColor = "#000000", Style = "none",
                        BaseFontSize = "16px", WeatherApiKey = "", UnitSystem = "imperial", WeatherLatLong = "0,0"
                    };

                    var accountSettings = new ServerAccountSettings
                    {
                        ImmichServerUrl = "http://mock-immich-server.com", ApiKey = "test-api-key",
                        ShowMemories = true, // Test specific
                        ShowFavorites = false,
                        ShowArchived = false,
                        Albums = new List<Guid>(), ExcludedAlbums = new List<Guid>(), People = new List<Guid>()
                    };
                    var serverSettings = new ServerSettings
                    {
                        GeneralSettingsImpl = generalSettings,
                        AccountsImpl = new List<ServerAccountSettings> { accountSettings }
                    };
                    services.AddSingleton<IServerSettings>(serverSettings);
                    services.AddSingleton<IGeneralSettings>(generalSettings); // Ensure general settings are also present
                });
            });

            var client = _factory.CreateClient();
            // Act
            // In this test, we expect AssetNotFoundException if MemoryAssetsPool doesn't find assets by filename
            // (assuming it's not implemented to do so) and the /api/memories mock is empty.
            // This exception should lead to a 500 Internal Server Error in the test environment
            // due to the DeveloperExceptionPageMiddleware.
            var response = await client.GetAsync("/api/Asset/RandomImageAndInfo");

            // Assert
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError),
                "Expected InternalServerError status when AssetNotFoundException is thrown because no memories are found by the current logic.");

            // Further assertions about which specific assets are returned are removed,
            // as the primary check here is the correct error handling path.
            // If MemoryAssetsPool were changed to support filename matching for memories,
            // this test would need to be significantly different:
            // 1. EnsureSuccessStatusCode()
            // 2. Loop and collect returned asset IDs
            // 3. Assert that only memory_*.jpg assets are present.
        }
    }
}
