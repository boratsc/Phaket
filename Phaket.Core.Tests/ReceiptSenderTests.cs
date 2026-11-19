using Phaket.Core.Mods;
using Phaket.Core.Omrl;

namespace Phaket.Core.Tests;

public class ReceiptSenderTests
{
    [Theory]
    [InlineData("installed", null)]
    [InlineData("failed", "VALIDATION_FAILED")]
    [InlineData("uninstalled", null)]
    public async Task SendsReceiptFields(string result, string? errorCode)
    {
        var transport = new FakeTransport();
        await new ReceiptSender(transport).SendAsync("valheim", "mod.full", "1.2", "steam", result, errorCode);
        var receipt = Assert.IsType<ModReceipt>(transport.Receipt);
        Assert.Equal("valheim", receipt.GameId);
        Assert.Equal("mod.full", receipt.ModFullName);
        Assert.Equal("1.2", receipt.ModVersion);
        Assert.Equal("steam", receipt.Store);
        Assert.Equal(result, receipt.Result);
        Assert.Equal(errorCode, receipt.ErrorCode);
        Assert.Contains(receipt.Os, new[] { "windows", "linux" });
        Assert.Equal("0.1.0", receipt.AppVersion);
    }

    [Fact]
    public async Task InvalidStoreBecomesNullAndTransportFailureIsIgnored()
    {
        var transport = new FakeTransport { Throw = true };
        await new ReceiptSender(transport).SendAsync("among-us", "mod", "1", "other", "installed");
        Assert.Null(transport.Receipt!.Store);
    }

    [Fact]
    public async Task CancellationIsIgnored()
    {
        await new ReceiptSender(new FakeTransport { Throw = true }).SendAsync("game", "mod", "1", null,
            "installed", cancellationToken: new CancellationToken(true));
    }

    [Fact]
    public async Task NullStoreIsAccepted()
    {
        var transport = new FakeTransport();
        await new ReceiptSender(transport).SendAsync("game", "mod", "1", null, "installed");
        Assert.Null(transport.Receipt!.Store);
    }

    [Fact]
    public async Task PhaketExceptionUsesStableErrorCode()
    {
        var transport = new FakeTransport();
        await new ReceiptSender(transport).SendAsync("game", "mod", "1", null, "failed",
            new PhaketException(ErrorCodes.ValidationFailed).Error.Code);
        Assert.Equal(ErrorCodes.ValidationFailed, transport.Receipt!.ErrorCode);
    }

    private sealed class FakeTransport : IReceiptTransport
    {
        public ModReceipt? Receipt { get; private set; }
        public bool Throw { get; init; }
        public Task PostAsync(string path, ModReceipt receipt, CancellationToken cancellationToken)
        {
            Assert.Equal("/receipts", path);
            Receipt = receipt;
            if (Throw) throw new OperationCanceledException();
            return Task.CompletedTask;
        }
    }
}
