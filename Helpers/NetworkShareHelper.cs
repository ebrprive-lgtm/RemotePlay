using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using RemotePlay.Helpers;

namespace RemotePlay;

[ExcludeFromCodeCoverage]
internal static class NetworkShareHelper
{
    private const int RESOURCETYPE_DISK = 1;

    /// <summary>
    /// For each path that matches a configured <see cref="NetworkShareCredential"/>,
    /// calls <c>WNetAddConnection2</c> so the process can access the UNC share even
    /// when the interactive-session credentials are not available.
    /// </summary>
    internal static void EnsureConnected(IEnumerable<string> paths, NetworkShareCredential[] credentials)
    {
        if (!OperatingSystem.IsWindows() || credentials.Length == 0)
            return;

        foreach (var path in paths)
        {
            var cred = FindMatchingCredential(path, credentials);
            if (cred is null) continue;

            var share = ExtractUncShare(path);
            if (share is null) continue;

            TryConnect(share, cred);
        }
    }

    private static NetworkShareCredential? FindMatchingCredential(string path, NetworkShareCredential[] credentials) =>
        credentials
            .Where(c => !string.IsNullOrWhiteSpace(c.Path))
            .FirstOrDefault(c => path.StartsWith(c.Path, StringComparison.OrdinalIgnoreCase));

    private static void TryConnect(string share, NetworkShareCredential cred)
    {
        var resource = new NativeMethods.NETRESOURCE
        {
            dwType       = RESOURCETYPE_DISK,
            lpRemoteName = share,
        };

        var result = NativeMethods.WNetAddConnection2(ref resource, cred.Password, cred.Username, 0);
        if (result == 0)
            Logger.Info($"Connected to network share: {share}");
        else
            Logger.Warning("NetworkShare", $"WNetAddConnection2 returned error {result} (0x{result:X8}) for '{share}'");
    }

    /// <summary>Extracts the <c>\\server\share</c> root from a UNC path, or returns <see langword="null"/> for non-UNC paths.</summary>
    private static string? ExtractUncShare(string path)
    {
        if (!path.StartsWith(@"\\", StringComparison.Ordinal))
            return null;

        // \\server\share\... → split on \ after stripping the leading \\
        var parts = path[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return null;

        return $@"\\{parts[0]}\{parts[1]}";
    }
}
