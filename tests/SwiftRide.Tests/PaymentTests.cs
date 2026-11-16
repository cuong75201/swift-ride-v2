using MassTransit;
using Moq;
using Payment_Service.Application.Contracts;
using Payment_Service.Application.Services;
using Payment_Service.Application.Services.Impl;
using Payment_Service.Domain;
using Payment_Service.Infrastructure.Persistence.Impl;
using SwiftRide.Contracts.Events;

namespace SwiftRide.Tests;

public sealed class PaymentTests
{
    [Fact]
    public async Task GetByUuid_returns_null_when_missing()
    {
        var service = new PaymentService(Mock.Of<IPaymentRepository>(), Mock.Of<IPaymentTransactionService>());

        Assert.Null(await service.GetByUuidAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task GetByUuid_maps_payment_and_transactions()
    {
        var payment = CreatePayment();
        var repository = new Mock<IPaymentRepository>();
        repository.Setup(x => x.GetPaymentByUuidAsync(payment.Uuid, It.IsAny<CancellationToken>()))
            .ReturnsAsync(payment);
        var transaction = new PaymentTransactionResponseContract
        {
            Uuid = Guid.NewGuid(), PaymentUuid = payment.Uuid, ReferenceCode = "REF", Amount = payment.Amount
        };
        var transactions = new Mock<IPaymentTransactionService>();
        transactions.Setup(x => x.GetByPaymentUuidAsync(payment.Uuid, It.IsAny<CancellationToken>()))
            .ReturnsAsync([transaction]);
        var service = new PaymentService(repository.Object, transactions.Object);

        var result = await service.GetByUuidAsync(payment.Uuid, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(payment.TripUuid, result.TripUuid);
        Assert.Equal(payment.Amount, result.Amount);
        Assert.Same(transaction, Assert.Single(result.Transactions));
    }

    [Fact]
    public async Task Payment_lists_are_mapped_for_rider_and_driver()
    {
        var payment = CreatePayment();
        var repository = new Mock<IPaymentRepository>();
        repository.Setup(x => x.GetPaymentsByRiderUuidAsync(payment.RiderUuid, It.IsAny<CancellationToken>()))
            .ReturnsAsync([payment]);
        repository.Setup(x => x.GetPaymentsByDriverUuidAsync(payment.DriverUuid, It.IsAny<CancellationToken>()))
            .ReturnsAsync([payment]);
        var service = new PaymentService(repository.Object, Mock.Of<IPaymentTransactionService>());

        var riderResult = Assert.Single(await service.GetByRiderUuidAsync(payment.RiderUuid, CancellationToken.None));
        var driverResult = Assert.Single(await service.GetByDriverUuidAsync(payment.DriverUuid, CancellationToken.None));

        Assert.Equal(payment.Uuid, riderResult.Uuid);
        Assert.Equal(payment.TransferCode, driverResult.TransferCode);
    }

    [Fact]
    public async Task ProcessIpn_ignores_outgoing_transfer()
    {
        var fixture = CreateTransactionService();
        var result = await fixture.Service.ProcessIpnAsync(
            ValidIpn() with { TransferType = "out" }, CancellationToken.None);

        Assert.Equal(PaymentIpnOutcome.Ignored, result.Outcome);
        fixture.Transactions.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(0, "REF")]
    [InlineData(100000, "")]
    public async Task ProcessIpn_rejects_invalid_amount_or_reference(int amount, string reference)
    {
        var fixture = CreateTransactionService();
        var result = await fixture.Service.ProcessIpnAsync(
            ValidIpn() with { TransferAmount = amount, ReferenceCode = reference }, CancellationToken.None);

        Assert.Equal(PaymentIpnOutcome.Invalid, result.Outcome);
    }

    [Theory]
    [InlineData(PaymentTransactionStatus.Succeeded, PaymentIpnOutcome.AlreadyProcessed)]
    [InlineData(PaymentTransactionStatus.Refunded, PaymentIpnOutcome.RefundRequired)]
    public async Task ProcessIpn_is_idempotent_for_existing_reference(
        PaymentTransactionStatus status, PaymentIpnOutcome expected)
    {
        var fixture = CreateTransactionService();
        fixture.Transactions.Setup(x => x.GetByReferenceCodeAsync("REF-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentTransaction { ReferenceCode = "REF-1", Status = status });

        var result = await fixture.Service.ProcessIpnAsync(ValidIpn(), CancellationToken.None);

        Assert.Equal(expected, result.Outcome);
        fixture.Payments.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ProcessIpn_rejects_missing_transfer_code()
    {
        var fixture = CreateTransactionService();
        var result = await fixture.Service.ProcessIpnAsync(
            ValidIpn() with { Code = null, Content = "no code", Description = "none" }, CancellationToken.None);

        Assert.Equal(PaymentIpnOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public async Task ProcessIpn_returns_not_found_for_unknown_payment()
    {
        var fixture = CreateTransactionService();

        var result = await fixture.Service.ProcessIpnAsync(ValidIpn(), CancellationToken.None);

        Assert.Equal(PaymentIpnOutcome.PaymentNotFound, result.Outcome);
        fixture.Payments.Verify(x => x.GetPaymentByCodeAsync("ABC123", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(true, 100000)]
    [InlineData(false, 99999)]
    public async Task ProcessIpn_records_refund_for_settled_or_wrong_amount(bool alreadySettled, int amount)
    {
        var payment = CreatePayment();
        payment.Status = alreadySettled ? PaymentStatus.Settled : PaymentStatus.Pending;
        var fixture = CreateTransactionService(payment);

        var result = await fixture.Service.ProcessIpnAsync(
            ValidIpn() with { TransferAmount = amount }, CancellationToken.None);

        Assert.Equal(PaymentIpnOutcome.RefundRequired, result.Outcome);
        fixture.Transactions.Verify(x => x.CreateAsync(
            It.Is<PaymentTransaction>(t =>
                t.PaymentUuid == payment.Uuid && t.Status == PaymentTransactionStatus.Refunded && t.Reason != null),
            It.IsAny<CancellationToken>()), Times.Once);
        fixture.Publisher.Verify(x => x.Publish(It.IsAny<PaymentSettled>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessIpn_settles_exact_payment_records_transaction_and_publishes_event()
    {
        var payment = CreatePayment();
        var fixture = CreateTransactionService(payment);

        var result = await fixture.Service.ProcessIpnAsync(ValidIpn(), CancellationToken.None);

        Assert.Equal(PaymentIpnOutcome.Processed, result.Outcome);
        Assert.Equal(PaymentStatus.Settled, payment.Status);
        fixture.Transactions.Verify(x => x.CreateAsync(
            It.Is<PaymentTransaction>(t =>
                t.PaymentUuid == payment.Uuid && t.Status == PaymentTransactionStatus.Succeeded && t.Amount == 100_000),
            It.IsAny<CancellationToken>()), Times.Once);
        fixture.Publisher.Verify(x => x.Publish(
            It.Is<PaymentSettled>(e => e.TripUuid == payment.TripUuid), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessIpn_extracts_code_from_content_case_insensitively()
    {
        var payment = CreatePayment();
        var fixture = CreateTransactionService(payment);

        await fixture.Service.ProcessIpnAsync(
            ValidIpn() with { Code = null, Content = "thanh toan sevqr abc123 cam on" }, CancellationToken.None);

        fixture.Payments.Verify(x => x.GetPaymentByCodeAsync("ABC123", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetTransactions_maps_domain_transactions()
    {
        var payment = CreatePayment();
        var transaction = new PaymentTransaction
        {
            Uuid = Guid.NewGuid(), PaymentUuid = payment.Uuid, ReferenceCode = "REF",
            Amount = 100_000, Status = PaymentTransactionStatus.Succeeded
        };
        var repository = new Mock<IPaymentTransactionRepository>();
        repository.Setup(x => x.GetByPaymentUuidAsync(payment.Uuid, It.IsAny<CancellationToken>()))
            .ReturnsAsync([transaction]);
        var service = new PaymentTransactionService(
            repository.Object, Mock.Of<IPaymentRepository>(), Mock.Of<IPublishEndpoint>());

        var result = Assert.Single(await service.GetByPaymentUuidAsync(payment.Uuid, CancellationToken.None));

        Assert.Equal(transaction.Uuid, result.Uuid);
        Assert.Equal(transaction.ReferenceCode, result.ReferenceCode);
        Assert.Equal(transaction.Status, result.Status);
    }

    private static TransactionFixture CreateTransactionService(Payment? payment = null)
    {
        var transactions = new Mock<IPaymentTransactionRepository>();
        var payments = new Mock<IPaymentRepository>();
        if (payment is not null)
        {
            payments.Setup(x => x.GetPaymentByCodeAsync("ABC123", It.IsAny<CancellationToken>()))
                .ReturnsAsync(payment);
        }
        var publisher = new Mock<IPublishEndpoint>();
        return new TransactionFixture(
            new PaymentTransactionService(transactions.Object, payments.Object, publisher.Object),
            transactions, payments, publisher);
    }

    private static Payment CreatePayment() => new()
    {
        Uuid = Guid.NewGuid(), TripUuid = Guid.NewGuid(), RiderUuid = Guid.NewGuid(), DriverUuid = Guid.NewGuid(),
        Amount = 100_000, QrUrl = "https://example.test/qr", TransferCode = "ABC123", Status = PaymentStatus.Pending
    };

    private static SepayIpnBuilder ValidIpn() => new()
    {
        TransferType = "in", TransferAmount = 100_000, ReferenceCode = "REF-1",
        Code = "ABC123", Content = "", Description = ""
    };

    private sealed record TransactionFixture(
        PaymentTransactionService Service,
        Mock<IPaymentTransactionRepository> Transactions,
        Mock<IPaymentRepository> Payments,
        Mock<IPublishEndpoint> Publisher);

    private sealed record SepayIpnBuilder
    {
        public string TransferType { get; init; } = "";
        public int TransferAmount { get; init; }
        public string ReferenceCode { get; init; } = "";
        public string? Code { get; init; }
        public string Content { get; init; } = "";
        public string Description { get; init; } = "";

        public static implicit operator SepayIpnContract(SepayIpnBuilder source) => new()
        {
            TransferType = source.TransferType,
            TransferAmount = source.TransferAmount,
            ReferenceCode = source.ReferenceCode,
            Code = source.Code,
            Content = source.Content,
            Description = source.Description
        };
    }
}
