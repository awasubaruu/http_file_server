using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace HttpFileServer.Server;

public static class NetworkService
{
    public sealed record NicInfo(string Ip, string Name, bool HasGateway);

    /// <summary>枚举本机所有可用网卡上的 IPv4 局域网地址（排除环回、隧道、APIPA 自配地址）。</summary>
    public static List<NicInfo> GetLanAddresses()
    {
        var result = new List<NicInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            bool hasGateway = nic.GetIPProperties().GatewayAddresses.Count > 0;

            foreach (var ip in nic.GetIPProperties().UnicastAddresses)
            {
                if (ip.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;
                var text = ip.Address.ToString();
                if (text.StartsWith("169.254.")) // 未连上网段时的自动私有地址
                    continue;
                if (seen.Add(text))
                    result.Add(new NicInfo(text, nic.Name, hasGateway));
            }
        }
        return result;
    }

    /// <summary>优先选有网关的网卡地址，用于生成二维码。</summary>
    public static string? PickPrimary(List<NicInfo> list) =>
        list.FirstOrDefault(n => n.HasGateway)?.Ip ?? list.FirstOrDefault()?.Ip;

    /// <summary>从 start 开始找一个可绑定的端口，最多向后尝试 50 个。</summary>
    public static int FindAvailablePort(int start, string listenAddress)
    {
        for (int port = start; port < start + 50; port++)
        {
            var ip = string.IsNullOrWhiteSpace(listenAddress) ? IPAddress.Any : IPAddress.Parse(listenAddress);
            try
            {
                var listener = new TcpListener(ip, port);
                listener.Start();
                listener.Stop();
                return port;
            }
            catch (SocketException)
            {
            }
        }
        return start;
    }
}
