using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace AgentSpeed.Core.Remote;

/// <summary>Explicit, user-scoped installation over the same verified SSH transport as monitoring.</summary>
public sealed class RemoteDeployment(Func<string, Stream?> openAsset)
{
    public const int MaxPackageBytes = 64 * 1024 * 1024;
    public static IReadOnlyList<string> Runtimes { get; } = Array.AsReadOnly(new[]
        { "linux-x64", "linux-arm64", "osx-x64", "osx-arm64", "win-x64", "win-arm64" });
    internal Func<ProcessStartInfo, ProcessStartInfo> ConfigureStart { get; init; } = info => info;

    public async Task<RemoteHost> DeployAsync(RemoteHost host, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!host.IsValid) throw new ArgumentException("Invalid remote host configuration.");
        progress?.Report("probing");
        // This plain command is safe through cmd.exe too; send a quoted sh script only to a POSIX host.
        var platform = await Run(host, "uname -s", null, TimeSpan.FromSeconds(25), cancellationToken).ConfigureAwait(false);
        if (platform.ExitCode == 255) EnsureSuccess(platform);
        var shell = platform.ExitCode == 0 && platform.Output.Trim() is "Linux" or "Darwin" ? "posix" : "powershell";
        var probe = await Run(host, Command("probe", shell), null, TimeSpan.FromSeconds(25), cancellationToken).ConfigureAwait(false);
        EnsureSuccess(probe);
        var rid = ParseProbe(probe.Output);
        if ((shell == "powershell") != rid.StartsWith("win-", StringComparison.Ordinal))
            throw new IOException("Remote shell and operating system do not match.");

        var file = $"AgentSpeed-Collector-{rid}.tar.gz";
        using var manifest = openAsset("SHA256SUMS.txt") ?? throw new IOException("Bundled collectors are missing. Use an official packaged build or build the collector bundles first.");
        using var manifestReader = new StreamReader(manifest);
        var hash = ParseHash(await ReadBounded(manifestReader, 4096, cancellationToken).ConfigureAwait(false), file);
        using var source = openAsset(file) ?? throw new IOException("Bundled collector is missing: " + file);
        using var package = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (package.Length + count > MaxPackageBytes) throw new IOException("Collector package exceeds the size limit.");
            package.Write(buffer, 0, count);
        }
        if (!Convert.ToHexString(SHA256.HashData(package.GetBuffer().AsSpan(0, (int)package.Length))).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Bundled collector SHA-256 mismatch.");
        package.Position = 0;
        progress?.Report("uploading");
        var install = await Run(host, Command("install", shell, hash), package, TimeSpan.FromMinutes(3), cancellationToken).ConfigureAwait(false);
        EnsureSuccess(install);
        var executable = ParseInstalled(install.Output, shell);
        return host with { Executable = executable, Shell = shell, Enabled = true };
    }

    internal static string ParseProbe(string output)
    {
        var lines = Lines(output);
        return lines.Length == 2 && lines[0] == "AGENTSPEED_DEPLOY_V1" && Runtimes.Contains(lines[1])
            ? lines[1] : throw new IOException("Invalid or unsupported remote platform response. Check that the SSH shell does not print a banner.");
    }

    internal static string ParseInstalled(string output, string shell)
    {
        var lines = Lines(output);
        if (lines.Length != 2 || lines[0] != "AGENTSPEED_INSTALLED_V1" || lines[1].Length is 0 or > 1024 || lines[1].Any(char.IsControl)
            || (shell == "powershell" ? !(lines[1].Length > 3 && char.IsAsciiLetter(lines[1][0]) && lines[1][1] == ':' && lines[1][2] == '\\') : !lines[1].StartsWith('/')))
            throw new IOException("Invalid collector installation response.");
        return lines[1];
    }

    internal static string ParseHash(string manifest, string file)
    {
        var matches = Lines(manifest).Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 2 && parts[1] == file).ToArray();
        if (matches.Length != 1 || matches[0][0].Length != 64 || !matches[0][0].All(char.IsAsciiHexDigit))
            throw new IOException("Invalid collector checksum manifest.");
        return matches[0][0].ToLowerInvariant();
    }

    private static string[] Lines(string output) => output.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');

    internal static string Command(string action, string shell, string? hash = null)
    {
        if (action is not ("probe" or "install") || shell is not ("posix" or "powershell")
            || action == "install" && (hash is not { Length: 64 } || !hash.All(char.IsAsciiHexDigit)))
            throw new ArgumentException("Invalid deployment command.");
        var name = $"AgentSpeed.Deploy.{action}-{shell}." + (shell == "posix" ? "sh" : "ps1");
        using var resource = typeof(RemoteDeployment).Assembly.GetManifestResourceStream(name) ?? throw new IOException("Missing deployment script.");
        using var reader = new StreamReader(resource);
        var script = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal).Replace("__SHA256__", hash ?? "", StringComparison.Ordinal);
        return shell == "powershell"
            ? "powershell.exe -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script))
            : "sh -c '" + script.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    private sealed record Result(int ExitCode, string Output, string Error);
    private static void EnsureSuccess(Result result)
    {
        if (result.ExitCode == 0) return;
        var detail = new string(result.Error.Where(c => !char.IsControl(c) || c == '\n').Take(2000).ToArray()).Trim();
        throw new IOException($"SSH deployment failed (exit {result.ExitCode}). {detail}");
    }

    private async Task<Result> Run(RemoteHost host, string command, Stream? input, TimeSpan timeout, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        token.ThrowIfCancellationRequested();
        using var child = new Process { StartInfo = ConfigureStart(host.CommandStartInfo(command)) };
        child.Start();
        void Kill() { try { if (!child.HasExited) child.Kill(entireProcessTree: true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { } }
        using var registration = deadline.Token.Register(Kill);
        async Task<T> Guard<T>(Task<T> task)
        {
            try { return await task.ConfigureAwait(false); }
            catch { deadline.Cancel(); throw; }
        }
        async Task Upload()
        {
            try
            {
                if (input is not null) await input.CopyToAsync(child.StandardInput.BaseStream, deadline.Token).ConfigureAwait(false);
            }
            catch { deadline.Cancel(); throw; }
            finally { child.StandardInput.Close(); }
        }
        try
        {
            var output = Guard(ReadBounded(child.StandardOutput, 65536, deadline.Token));
            var error = Guard(ReadBounded(child.StandardError, 65536, deadline.Token));
            await Task.WhenAll(Upload(), output, error, child.WaitForExitAsync(deadline.Token)).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return new(child.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        catch (Exception) when (token.IsCancellationRequested)
        { throw new OperationCanceledException(token); }
        catch (Exception e) when (deadline.IsCancellationRequested && e is OperationCanceledException)
        { throw new IOException("SSH deployment timed out.", e); }
        finally { Kill(); }
    }

    private static async Task<string> ReadBounded(StreamReader reader, int limit, CancellationToken token)
    {
        var result = new StringBuilder();
        var buffer = new char[2048];
        int count;
        while ((count = await reader.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (result.Length + count > limit) throw new IOException("Deployment response exceeds the size limit.");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }
}
