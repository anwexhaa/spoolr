using Spoolr.Core.Printers;

namespace Spoolr.UnitTests.Printers;

public sealed class PrinterTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    private static Printer NewPrinter() =>
        Printer.Register(
            name: "HQ-Floor3-Colour",
            location: "Hyderabad / Floor 3",
            model: "Contoso LaserJet 9000",
            deviceKeyHash: "pbkdf2$stub",
            Now,
            supportsColor: true,
            supportsDuplex: true);

    [Fact]
    public void Register_StartsUnknownUntilTheDeviceChecksIn()
    {
        var printer = NewPrinter();

        Assert.Equal(PrinterStatus.Unknown, printer.ReportedStatus);
        Assert.Equal(PrinterStatus.Unknown, printer.StatusAt(Now));
        Assert.Null(printer.LastHeartbeatAt);
        Assert.False(printer.CanAcceptWorkAt(Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Register_RejectsABlankName(string name) =>
        Assert.Throws<ArgumentException>(() => Printer.Register(
            name, "loc", "model", "hash", Now));

    [Fact]
    public void Register_RejectsANonPositivePageLimit() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Printer.Register(
            "name", "loc", "model", "hash", Now, maxPagesPerJob: 0));

    [Fact]
    public void Heartbeat_RecordsTheReportedStatusAndTime()
    {
        var printer = NewPrinter();

        printer.Heartbeat(PrinterStatus.Online, Now);

        Assert.Equal(PrinterStatus.Online, printer.ReportedStatus);
        Assert.Equal(Now, printer.LastHeartbeatAt);
        Assert.True(printer.CanAcceptWorkAt(Now));
    }

    [Fact]
    public void StatusAt_InsideTheHeartbeatWindow_KeepsTheReportedStatus()
    {
        var printer = NewPrinter();
        printer.Heartbeat(PrinterStatus.Online, Now);

        Assert.Equal(PrinterStatus.Online, printer.StatusAt(Now.AddMinutes(1)));
        Assert.Equal(PrinterStatus.Online, printer.StatusAt(Now.AddMinutes(2)));
    }

    [Fact]
    public void StatusAt_PastTheHeartbeatWindow_ReportsOfflineWithoutAnyUpdate()
    {
        var printer = NewPrinter();
        printer.Heartbeat(PrinterStatus.Online, Now);

        // Nobody told the service the device died. Elapsed time is enough.
        Assert.Equal(PrinterStatus.Offline, printer.StatusAt(Now.AddMinutes(3)));
        Assert.False(printer.CanAcceptWorkAt(Now.AddMinutes(3)));
    }

    [Fact]
    public void StatusAt_HonoursACustomHeartbeatWindow()
    {
        var printer = NewPrinter();
        printer.Heartbeat(PrinterStatus.Online, Now);

        var window = TimeSpan.FromSeconds(30);

        Assert.Equal(PrinterStatus.Online, printer.StatusAt(Now.AddSeconds(29), window));
        Assert.Equal(PrinterStatus.Offline, printer.StatusAt(Now.AddSeconds(31), window));
    }

    [Fact]
    public void Degraded_DeviceIsLiveButNotGivenWork()
    {
        var printer = NewPrinter();
        printer.Heartbeat(PrinterStatus.Degraded, Now);

        Assert.Equal(PrinterStatus.Degraded, printer.StatusAt(Now));
        Assert.False(printer.CanAcceptWorkAt(Now));
    }

    [Theory]
    [InlineData(PrinterStatus.Retired)]
    [InlineData(PrinterStatus.Unknown)]
    public void Heartbeat_RejectsStatusesADeviceCannotClaim(PrinterStatus reported)
    {
        var printer = NewPrinter();

        Assert.Throws<ArgumentOutOfRangeException>(() => printer.Heartbeat(reported, Now));
    }

    [Fact]
    public void Heartbeat_OnARetiredPrinter_Throws()
    {
        var printer = NewPrinter();
        printer.Retire();

        Assert.Throws<InvalidOperationException>(() => printer.Heartbeat(PrinterStatus.Online, Now));
    }

    [Fact]
    public void Retire_TakesTheDeviceOutOfServicePermanently()
    {
        var printer = NewPrinter();
        printer.Heartbeat(PrinterStatus.Online, Now);

        printer.Retire();

        Assert.Equal(PrinterStatus.Retired, printer.StatusAt(Now));
        Assert.False(printer.CanAcceptWorkAt(Now));
    }

    [Fact]
    public void RotateDeviceKey_ReplacesTheStoredHash()
    {
        var printer = NewPrinter();
        var before = printer.DeviceKeyHash;

        printer.RotateDeviceKey("pbkdf2$rotated");

        Assert.NotEqual(before, printer.DeviceKeyHash);
        Assert.Equal("pbkdf2$rotated", printer.DeviceKeyHash);
    }

    [Fact]
    public void RotateDeviceKey_RejectsABlankHash() =>
        Assert.Throws<ArgumentException>(() => NewPrinter().RotateDeviceKey("   "));
}
