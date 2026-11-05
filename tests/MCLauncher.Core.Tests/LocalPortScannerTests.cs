using MCLauncher.Core.Connect;

namespace MCLauncher.Core.Tests;

public sealed class LocalPortScannerTests
{
    [Fact]
    public void 能列出本机监听的TCP端口()
    {
        var ports = LocalPortScanner.GetListeningTcpPorts();

        Assert.NotEmpty(ports);
        Assert.All(ports, port => Assert.InRange(port.Port, 1, 65535));
        Assert.Equal(ports.Count, ports.Select(port => port.Port).Distinct().Count());
    }
}
