using System.Runtime.InteropServices;
using System.Text;

namespace MeowSSH.App;

internal static partial class HevSocks5TunnelNative
{
    private const string Library = "hev-socks5-tunnel";

    [LibraryImport(Library, EntryPoint = "hev_socks5_tunnel_main_from_str")]
    private static partial int MainFromString(byte[] config, uint length, int tunFd);

    [LibraryImport(Library, EntryPoint = "hev_socks5_tunnel_quit")]
    internal static partial void Quit();

    internal static int Run(string config, int tunFd)
    {
        var bytes = Encoding.UTF8.GetBytes(config);
        return MainFromString(bytes, checked((uint)bytes.Length), tunFd);
    }
}
