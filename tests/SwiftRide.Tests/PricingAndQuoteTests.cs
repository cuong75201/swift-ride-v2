using MatchingPricing_Service.Application.Contracts;
using MatchingPricing_Service.Application.Services;
using MatchingPricing_Service.Application.Services.Impl;
using MatchingPricing_Service.Application.Services.Pricing;
using MatchingPricing_Service.Application.Services.Util;
using MatchingPricing_Service.Domain;
using Microsoft.Extensions.Options;
using Moq;

namespace SwiftRide.Tests;

public sealed class PricingAndQuoteTests
{
    private static readonly PricingOptions Options = new()
    {
        PricePerKilometer = 5_000m,
        PricePerMinute = 1_000m,
        Toll = 10_000m,
        TaxRate = 0.1m,
        NormalMultiplier = 1m,
        RainMultiplier = 1.5m
    };

    private static PricingService CreatePricingService() => new(
        [new NormalPricingStrategy(Microsoft.Extensions.Options.Options.Create(Options)),
         new RainPricingStrategy(Microsoft.Extensions.Options.Options.Create(Options))]);

    [Theory]
    [InlineData(WeatherCondition.Normal, 1, 77_000)]
    [InlineData(WeatherCondition.Rain, 1, 110_000)]
    [InlineData(WeatherCondition.Normal, 0.5, 38_500)]
    [InlineData(WeatherCondition.Normal, 0, 0)]
    public void ProcessPrice_calculates_expected_total(
        WeatherCondition weather, double promotion, decimal expected)
    {
        var result = CreatePricingService().ProcessPrice(10, 10, weather, (decimal)promotion);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(double.NaN, 1)]
    [InlineData(double.PositiveInfinity, 1)]
    [InlineData(1, -1)]
    [InlineData(1, double.NaN)]
    public void ProcessPrice_rejects_invalid_distance_or_duration(double distance, double duration)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CreatePricingService().ProcessPrice(distance, duration, WeatherCondition.Normal));
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void ProcessPrice_rejects_invalid_promotion_multiplier(double promotion)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CreatePricingService().ProcessPrice(1, 1, WeatherCondition.Normal, (decimal)promotion));
    }

    [Fact]
    public void PricingService_rejects_unknown_weather_condition()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CreatePricingService().GetMultiplier((WeatherCondition)999));
    }

    [Fact]
    public void PricingService_rejects_negative_strategy_result()
    {
        var strategy = new Mock<IPricingStrategy>();
        strategy.SetupGet(x => x.WeatherCondition).Returns(WeatherCondition.Normal);
        strategy.Setup(x => x.Calculate(It.IsAny<PricingContext>())).Returns(-1);
        var service = new PricingService([strategy.Object]);

        Assert.Throws<InvalidOperationException>(() =>
            service.ProcessPrice(1, 1, WeatherCondition.Normal));
    }

    [Fact]
    public async Task RequestQuote_converts_units_and_returns_first_route()
    {
        var route = new OSRMRouteContract { Distance = 12_500, Duration = 1_500 };
        var osrm = new Mock<IOSRMService>();
        osrm.Setup(x => x.GetRouteAsync(It.IsAny<IReadOnlyList<LocationContract>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OSRMResponseContract { Routes = [route] });
        var pricing = new Mock<IPricingService>();
        pricing.Setup(x => x.ProcessPrice(12.5, 25, WeatherCondition.Rain, 1)).Returns(123_456m);
        pricing.Setup(x => x.GetMultiplier(WeatherCondition.Rain)).Returns(1.5m);
        var service = new QuoteService(osrm.Object, pricing.Object);

        var result = await service.RequestQuoteAsync(new QuoteRequestContract
        {
            Pickup = new LocationContract(10, 106),
            Destination = new LocationContract(11, 107),
            WeatherCondition = WeatherCondition.Rain
        }, CancellationToken.None);

        Assert.Same(route, result.Route);
        Assert.Equal(12.5, result.DistanceKM);
        Assert.Equal(25, result.DurationMin);
        Assert.Equal(123_456m, result.Price);
        Assert.Equal(1.5m, result.Multiplier);
        osrm.Verify(x => x.GetRouteAsync(
            It.Is<IReadOnlyList<LocationContract>>(locations =>
                locations.Count == 2 && locations[0].Latitude == 10 && locations[1].Longitude == 107),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RequestQuote_throws_when_osrm_has_no_route()
    {
        var osrm = new Mock<IOSRMService>();
        osrm.Setup(x => x.GetRouteAsync(It.IsAny<IReadOnlyList<LocationContract>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OSRMResponseContract());
        var service = new QuoteService(osrm.Object, Mock.Of<IPricingService>());

        await Assert.ThrowsAsync<InvalidDataException>(() => service.RequestQuoteAsync(
            new QuoteRequestContract
            {
                Pickup = new LocationContract(10, 106),
                Destination = new LocationContract(11, 107)
            }, CancellationToken.None));
    }
}
