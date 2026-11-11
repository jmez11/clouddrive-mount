using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using CloudDriveMount.Core.Models;

namespace CloudDriveMount.Core.Services;

public sealed class CloudflaredTunnelSession : IDisposable
{
    public Guid DriveId { get; init; }
    public string TargetHostname { get; init; } = string.Empty;
    public int LocalPort { get; init; }
    public Process? Process { get; init; }

    public void Dispose()
    {
        try
        {
            if (Process is { HasExited: false })
            {
                Process.Kill(entireProcessTree: true);
                Process.WaitForExit(2000);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
        finally
        {
            Process?.Dispose();
        }
    }
}

public class CloudflaredTunnelManager : IDisposable
{
    private readonly ConcurrentDictionary<Guid, CloudflaredTunnelSession> _sessions = new();

    public static string? ResolveCloudflaredPath(string? configuredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) &&
            !string.Equals(configuredPath, "cloudflared", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(configuredPath))
        {
            return configuredPath;
        }

        // Check PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "cloudflared.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // Ignore invalid path segments
            }
        }

        // Check common installation locations on Windows
        string[] commonLocations =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "cloudflared", "cloudflared.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "cloudflared", "cloudflared.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cloudflared", "cloudflared.exe"),
            Path.Combine(AppContext.BaseDirectory, "cloudflared.exe")
        ];

        foreach (var loc in commonLocations)
        {
            if (File.Exists(loc))
                return loc;
        }

        // Check WinGet packages folder
        try
        {
            var wingetDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(wingetDir))
            {
                var found = Directory.EnumerateFiles(wingetDir, "cloudflared.exe", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (found != null)
                    return found;
            }
        }
        catch
        {
            // Ignore search errors
        }

        return null;
    }

    public static int AllocateEphemeralPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static Dictionary<string, string> GetServiceTokenHeaders(DriveConfig config)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!config.UseProxy)
            return headers;

        if (config.ProxyMode is CloudflaredProxyMode.ServiceTokenHeaders or CloudflaredProxyMode.BothAccessTcpAndHeaders)
        {
            var clientId = config.CfAccessClientId?.Trim();
            var clientSecret = CredentialProtector.Unprotect(config.EncryptedCfAccessClientSecret).Trim();
            if (!string.IsNullOrEmpty(clientId) && !string.IsNullOrEmpty(clientSecret))
            {
                headers["CF-Access-Client-Id"] = clientId;
                headers["CF-Access-Client-Secret"] = clientSecret;
            }
        }

        return headers;
    }

    public static string BuildAccessTcpArguments(DriveConfig config, int localPort)
    {
        var targetHost = string.IsNullOrWhiteSpace(config.CloudflaredAccessHostname)
            ? config.Host.Trim()
            : config.CloudflaredAccessHostname.Trim();

        if (string.IsNullOrWhiteSpace(targetHost))
            throw new InvalidOperationException("Target hostname is required for cloudflared access tcp.");

        var args = new List<string>
        {
            "access",
            "tcp",
            "--hostname",
            QuoteArg(targetHost),
            "--url",
            $"127.0.0.1:{localPort}"
        };

        var clientId = config.CfAccessClientId?.Trim();
        var clientSecret = CredentialProtector.Unprotect(config.EncryptedCfAccessClientSecret).Trim();
        if (!string.IsNullOrEmpty(clientId) && !string.IsNullOrEmpty(clientSecret))
        {
            args.Add("--id");
            args.Add(QuoteArg(clientId));
            args.Add("--secret");
            args.Add(QuoteArg(clientSecret));
        }

        return string.Join(" ", args);
    }

    public async Task<CloudflaredTunnelSession> StartTunnelAsync(DriveConfig config, CancellationToken cancellationToken = default)
    {
        StopTunnel(config.Id);

        var binaryPath = ResolveCloudflaredPath(config.CloudflaredBinaryPath)
            ?? throw new FileNotFoundException(
                "Could not locate cloudflared.exe. Install Cloudflare Tunnel client (`winget install Cloudflare.cloudflared`) or specify its path in Proxy settings.");

        var localPort = AllocateEphemeralPort();
        var arguments = BuildAccessTcpArguments(config, localPort);

        var psi = new ProcessStartInfo
        {
            FileName = binaryPath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stderrBuffer = new List<string>();
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                lock (stderrBuffer)
                {
                    if (stderrBuffer.Count < 30)
                        stderrBuffer.Add(e.Data);
                }
            }
        };

        if (!process.Start())
            throw new InvalidOperationException("Failed to start cloudflared process.");

        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        // Wait up to 8 seconds for local listener port to accept TCP connections
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(8))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (process.HasExited)
            {
                string errText;
                lock (stderrBuffer)
                {
                    errText = string.Join(Environment.NewLine, stderrBuffer);
                }
                throw new InvalidOperationException(
                    $"cloudflared exited prematurely (code {process.ExitCode}). {errText}".Trim());
            }

            if (await IsPortListeningAsync(localPort, cancellationToken))
            {
                var session = new CloudflaredTunnelSession
                {
                    DriveId = config.Id,
                    TargetHostname = string.IsNullOrWhiteSpace(config.CloudflaredAccessHostname) ? config.Host : config.CloudflaredAccessHostname,
                    LocalPort = localPort,
                    Process = process
                };
                _sessions[config.Id] = session;
                config.ActiveProxyPort = localPort;
                return session;
            }

            await Task.Delay(150, cancellationToken);
        }

        try { process.Kill(true); } catch { }
        throw new TimeoutException($"Timed out waiting for cloudflared access tcp on 127.0.0.1:{localPort}.");
    }

    public void StopTunnel(Guid driveId)
    {
        if (_sessions.TryRemove(driveId, out var session))
        {
            session.Dispose();
        }
    }

    private static async Task<bool> IsPortListeningAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(250);
            await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string QuoteArg(string value)
    {
        return value.Contains(' ') ? $"\"{value}\"" : value;
    }

    public void Dispose()
    {
        foreach (var key in _sessions.Keys.ToList())
        {
            StopTunnel(key);
        }
    }
}
