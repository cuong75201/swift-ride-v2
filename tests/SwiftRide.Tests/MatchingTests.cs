using MatchingPricing_Service.Application.Contracts;
using MatchingPricing_Service.Application.Services;
using MatchingPricing_Service.Domain;
using MatchingPricing_Service.Infrastructure.Persistence.Impl;
using Microsoft.Extensions.Options;
using MongoDB.Driver.GeoJsonObjectModel;
using Moq;

namespace SwiftRide.Tests;

public sealed class MatchingTests
{
    [Fact]
    public async Task Match_returns_nearest_driver_and_forwards_filters()
    {
        var driverUuid = Guid.NewGuid();
        var excluded = new[] { Guid.NewGuid() };
        var repository = new Mock<IDriverLocationRepository>();
        repository.Setup(x => x.FindNearestAvailableAsync(
                It.IsAny<LocationContract>(), excluded, 7_500, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateLocation(driverUuid));
        var service = new MatchingDriver(repository.Object,
            Options.Create(new MatchingSetting { MaxDistanceMeters = 7_500 }));
        var request = new MatchingRequestContract
        {
            TripUuid = Guid.NewGuid(),
            Pickup = new LocationContract(10.5, 106.5),
            ExcludeDriverUuids = excluded
        };

        var result = await service.MatchAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(driverUuid, result.DriverUuid);
        Assert.Equal(request.TripUuid, result.TripUuid);
    }

    [Fact]
    public async Task Match_returns_unsuccessful_when_no_driver_exists()
    {
        var repository = new Mock<IDriverLocationRepository>();
        var service = new MatchingDriver(repository.Object, Options.Create(new MatchingSetting()));

        var result = await service.MatchAsync(new MatchingRequestContract
        {
            TripUuid = Guid.NewGuid(),
            Pickup = new LocationContract(10, 106)
        }, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(Guid.Empty, result.DriverUuid);
    }

    [Theory]
    [InlineData(-91, 0)]
    [InlineData(91, 0)]
    [InlineData(0, -181)]
    [InlineData(0, 181)]
    [InlineData(double.NaN, 0)]
    public async Task Match_rejects_invalid_coordinates(double latitude, double longitude)
    {
        var service = new MatchingDriver(Mock.Of<IDriverLocationRepository>(), Options.Create(new MatchingSetting()));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.MatchAsync(
            new MatchingRequestContract
            {
                TripUuid = Guid.NewGuid(),
                Pickup = new LocationContract(latitude, longitude)
            }, CancellationToken.None));
    }

    [Fact]
    public async Task Match_rejects_empty_trip_uuid()
    {
        var service = new MatchingDriver(Mock.Of<IDriverLocationRepository>(), Options.Create(new MatchingSetting()));

        await Assert.ThrowsAsync<ArgumentException>(() => service.MatchAsync(
            new MatchingRequestContract { Pickup = new LocationContract(10, 106) }, CancellationToken.None));
    }

    [Fact]
    public async Task DriverLocationService_maps_repository_entity()
    {
        var entity = CreateLocation(Guid.NewGuid());
        var repository = new Mock<IDriverLocationRepository>();
        repository.Setup(x => x.FindByDriverUuidAsync(entity.DriverUuid, It.IsAny<CancellationToken>()))
            .ReturnsAsync(entity);
        var service = new DriverLocationService(repository.Object);

        var result = await service.FindByDriverUuidAsync(entity.DriverUuid, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(entity.DriverUuid, result.DriverUuid);
        Assert.Equal(10.25, result.Latitude);
        Assert.Equal(106.75, result.Longitude);
        Assert.True(result.IsOnline);
        Assert.True(result.IsAvailable);
    }

    [Theory]
    [InlineData(-91, 0)]
    [InlineData(0, 181)]
    public async Task UpdateLocation_rejects_invalid_coordinates(double latitude, double longitude)
    {
        var service = new DriverLocationService(Mock.Of<IDriverLocationRepository>());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.UpdateLocationAsync(Guid.NewGuid(), latitude, longitude, CancellationToken.None));
    }

    [Fact]
    public async Task Availability_and_online_status_are_forwarded()
    {
        var driverUuid = Guid.NewGuid();
        var repository = new Mock<IDriverLocationRepository>();
        var service = new DriverLocationService(repository.Object);

        await service.UpdateAvailabilityAsync(driverUuid, true, CancellationToken.None);
        await service.UpdateOnlineStatus(driverUuid, false, CancellationToken.None);

        repository.Verify(x => x.SetAvailabilityAsync(driverUuid, true, CancellationToken.None), Times.Once);
        repository.Verify(x => x.SetOnlineStatusAsync(driverUuid, false, CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task DriverLocation_operations_reject_empty_uuid()
    {
        var service = new DriverLocationService(Mock.Of<IDriverLocationRepository>());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.FindByDriverUuidAsync(Guid.Empty, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UpdateAvailabilityAsync(Guid.Empty, true, CancellationToken.None));
    }

    private static DriverLocation CreateLocation(Guid driverUuid) => new()
    {
        DriverUuid = driverUuid,
        Location = new GeoJsonPoint<GeoJson2DGeographicCoordinates>(
            new GeoJson2DGeographicCoordinates(106.75, 10.25)),
        IsOnline = true,
        IsAvailable = true,
        UpdatedAt = DateTime.UtcNow
    };
}
