using System.IO;
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

    internal static (bool Success, string ErrorMessage) TestConnection(string path, string username, string password)
    {
        if (string.IsNullOrWhiteSpace(path))
            return (false, "Network share path is empty.");

        path = path.Trim();
        var share = ExtractUncShare(path);
        if (share is null)
            return (false, "Path must be a UNC path (for example, \\\\server\\share).");

        if (!OperatingSystem.IsWindows())
            return (false, "Network share testing is only supported on Windows.");

        if (!string.IsNullOrWhiteSpace(username) || !string.IsNullOrWhiteSpace(password))
        {
            var result = Connect(share, username, password);
            if (result != 0)
                return (false, GetConnectionErrorMessage(result));
        }

        if (!Directory.Exists(path))
            return (false, "Cannot access the network share. Check that the path exists and the account has permission.");

        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
            entries.MoveNext();
        }
        catch (UnauthorizedAccessException)
        {
            return (false, "Access denied while checking the network share. Check the account's permissions.");
        }
        catch (System.Security.SecurityException)
        {
            return (false, "Access denied while checking the network share. Check the account's permissions.");
        }
        catch (IOException ex)
        {
            return (false, $"Could not read the network share: {ex.Message}");
        }
        catch (ArgumentException)
        {
            return (false, "The network share path is invalid.");
        }
        catch (NotSupportedException)
        {
            return (false, "The network share path is invalid.");
        }

        return (true, string.Empty);
    }

    private static NetworkShareCredential? FindMatchingCredential(string path, NetworkShareCredential[] credentials) =>
        credentials
            .Where(c => !string.IsNullOrWhiteSpace(c.Path))
            .FirstOrDefault(c => path.StartsWith(c.Path, StringComparison.OrdinalIgnoreCase));

    private static void TryConnect(string share, NetworkShareCredential cred)
    {
        var result = Connect(share, cred.Username, cred.Password);
        if (result == 0)
            Logger.Info($"Connected to network share: {share}");
        else
            Logger.Warning("NetworkShare", $"WNetAddConnection2 returned error {result} (0x{result:X8}) for '{share}'");
    }

    private static int Connect(string share, string? username, string? password)
    {
        var resource = new NativeMethods.NETRESOURCE
        {
            dwType       = RESOURCETYPE_DISK,
            lpRemoteName = share,
        };

        return NativeMethods.WNetAddConnection2(ref resource, password, username, 0);
    }

    private static string GetConnectionErrorMessage(int errorCode) => errorCode switch
    {
        5 => "Access was denied. Check the username, password, and share permissions.",
        53 or 67 => "Network path was not found. Check the server and share names.",
        86 or 1326 => "Windows rejected the credentials. Check the username and password.",
        1219 => "Windows already has a connection to this server using different credentials. Disconnect it before testing another account.",
        _ => $"Windows could not connect to the network share (error code {errorCode}).",
    };

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
