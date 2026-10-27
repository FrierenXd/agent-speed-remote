using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using AgentSpeed.Core.Remote;

namespace AgentSpeed.Core.Tests;

public sealed class RemoteDeploymentTests
{
    [Theory]
    [InlineData("linux-x64")]
    [InlineData("linux-arm64")]
    [InlineData("osx-x64")]
    [InlineData("osx-arm64")]
    [InlineData("win-x64")]
    [InlineData("win-arm64")]
    public void ParsesAllSupportedPlatforms(string rid) =>
        Assert.Equal(rid, RemoteDeployment.ParseProbe($"AGENTSPEED_DEPLOY_V1\r\n{rid}\r\n"));

    [Theory]
    [InlineData("hello\nAGENTSPEED_DEPLOY_V1\nlinux-x64\n")]
    [InlineData("AGENTSPEED_DEPLOY_V1\nlinux-musl-x64\n")]
    [InlineData("AGENTSPEED_DEPLOY_V1\nlinux-x64\nextra")]
    public void RejectsUnsupportedPlatformsAndShellBanners(string output) =>
        Assert.Throws<IOException>(() => RemoteDeployment.ParseProbe(output));

    [Fact]
    public void ChecksumManifestMustHaveOneUnambiguousDigest()
    {
        var line = new string('a', 64) + "  AgentSpeed-Collector-linux-x64.tar.gz\n";
        Assert.Equal(new string('a', 64), RemoteDeployment.ParseHash(line, "AgentSpeed-Collector-linux-x64.tar.gz"));
        Assert.Throws<IOException>(() => RemoteDeployment.ParseHash(line + line, "AgentSpeed-Collector-linux-x64.tar.gz"));
        Assert.Throws<IOException>(() => RemoteDeployment.ParseHash("bad  package", "package"));
    }

    [Theory]
    [InlineData("agentspeed-collector", "posix")]
    [InlineData("../collector", "posix")]
    [InlineData("C:collector.exe", "powershell")]
    [InlineData("/tmp/collector\t", "posix")]
    public void RejectsNonAbsoluteOrUnsafeInstalledPaths(string path, string shell) =>
        Assert.Throws<IOException>(() => RemoteDeployment.ParseInstalled("AGENTSPEED_INSTALLED_V1\n" + path + "\n", shell));

    [Fact]
    public void InstallerCommandsAreEncodedAndHaveNoUnexpandedTemplate()
    {
        var hash = new string('a', 64);
        var posix = RemoteDeployment.Command("install", "posix", hash);
        Assert.StartsWith("sh -c '", posix);
        Assert.Contains(hash, posix);
        Assert.DoesNotContain('\r', posix);
        var windows = RemoteDeployment.Command("install", "powershell", hash);
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(windows.Split(' ').Last()));
        Assert.Contains(hash, script);
        Assert.Contains("OpenStandardInput", script);
        Assert.DoesNotContain("__SHA256__", script);
        Assert.Throws<ArgumentException>(() => RemoteDeployment.Command("install", "posix", "'; bad"));
        Assert.Contains("StrictHostKeyChecking=yes", new RemoteHost("one", "One", "host").CommandStartInfo(posix).ArgumentList);
    }

    [Fact]
    public async Task InstallsAndReusesVerifiedVersionWithoutOverwritingCustomPath()
    {
        if (OperatingSystem.IsWindows()) return;
        using var home = new TempDirectory();
        var package = Package("#!/bin/sh\n[ \"$1\" = --check ] || exit 2\nprintf 'AGENTSPEED_COLLECTOR_V1\\n'\n");
        var digest = Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant();
        var deploy = Create(home.Combine("home's space"), package, digest);
        var host = new RemoteHost("one", "One", "host", "/manual/collector", Enabled: false);
        var first = await deploy.DeployAsync(host);
        var second = await deploy.DeployAsync(host);
        Assert.Equal(first, second);
        Assert.Equal("/manual/collector", host.Executable);
        Assert.True(first.Enabled);
        Assert.Equal("posix", first.Shell);
        Assert.Contains(digest, first.Executable);
        Assert.True(File.Exists(first.Executable));
        var root = Path.GetDirectoryName(Path.GetDirectoryName(first.Executable))!;
        Assert.Empty(Directory.GetDirectories(root, ".upload.*"));
        Assert.Empty(Directory.GetDirectories(root, ".lock-*"));
    }

    [Fact]
    public async Task CorruptLocalArchiveIsNotUploaded()
    {
        if (OperatingSystem.IsWindows()) return;
        using var home = new TempDirectory();
        var deploy = Create(home.Combine("remote"), Package("invalid"), new string('a', 64));
        var error = await Assert.ThrowsAsync<IOException>(() => deploy.DeployAsync(new("one", "One", "host")));
        Assert.Contains("SHA-256 mismatch", error.Message);
        Assert.False(Directory.Exists(home.Combine("remote", ".local")));
    }

    [Fact]
    public async Task FailedRuntimeCheckCleansStagingWithoutPublishingVersion()
    {
        if (OperatingSystem.IsWindows()) return;
        using var home = new TempDirectory();
        var package = Package("#!/bin/sh\nexit 7\n");
        var digest = Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant();
        var deploy = Create(home.Combine("remote"), package, digest);
        await Assert.ThrowsAsync<IOException>(() => deploy.DeployAsync(new("one", "One", "host")));
        Assert.Empty(Directory.GetFileSystemEntries(home.Combine("remote", ".local", "lib", "agentspeed-collector", "releases")));
    }

    [Fact]
    public async Task WindowsAutoDetectionNeverSendsAPosixScriptThroughCmd()
    {
        if (OperatingSystem.IsWindows()) return;
        var commands = new List<string>();
        var package = Encoding.ASCII.GetBytes("test package");
        var hash = Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant();
        var deploy = new RemoteDeployment(name => new MemoryStream(name == "SHA256SUMS.txt"
            ? Encoding.ASCII.GetBytes($"{hash}  AgentSpeed-Collector-win-x64.tar.gz\n") : package))
        {
            ConfigureStart = info =>
            {
                var command = info.ArgumentList.Last(); commands.Add(command);
                if (command == "uname -s") return Shell("exit 1");
                Assert.StartsWith("powershell.exe -NoProfile -NonInteractive -EncodedCommand ", command);
                var script = Encoding.Unicode.GetString(Convert.FromBase64String(command.Split(' ').Last()));
                return Shell(script.Contains("OpenStandardInput", StringComparison.Ordinal)
                    ? "cat >/dev/null; printf '%s\\n' AGENTSPEED_INSTALLED_V1 'C:\\Users\\alice\\collector.exe'"
                    : "printf '%s\\n' AGENTSPEED_DEPLOY_V1 win-x64");
            },
        };
        var result = await deploy.DeployAsync(new("one", "One", "windows", Shell: "posix"));
        Assert.Equal("powershell", result.Shell);
        Assert.Equal(@"C:\Users\alice\collector.exe", result.Executable);
        Assert.Equal(3, commands.Count);
        Assert.DoesNotContain(commands, command => command.StartsWith("sh -c", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancellationTerminatesBlockedSshCommand()
    {
        if (OperatingSystem.IsWindows()) return;
        var deploy = new RemoteDeployment(_ => null) { ConfigureStart = _ => Shell("sleep 60") };
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => deploy.DeployAsync(new("one", "One", "host"), cancellationToken: stop.Token));
    }

    private static RemoteDeployment Create(string home, byte[] package, string hash) => new(name => new MemoryStream(
        name == "SHA256SUMS.txt" ? Encoding.ASCII.GetBytes(string.Join("\n", RemoteDeployment.Runtimes.Select(rid => $"{hash}  AgentSpeed-Collector-{rid}.tar.gz"))) : package))
    {
        ConfigureStart = original =>
        {
            var info = Shell(original.ArgumentList.Last());
            info.Environment["HOME"] = home;
            return info;
        },
    };

    private static ProcessStartInfo Shell(string command)
    {
        var info = new ProcessStartInfo("/bin/sh") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add("-c"); info.ArgumentList.Add(command); return info;
    }

    private static byte[] Package(string script)
    {
        using var result = new MemoryStream();
        using (var zip = new GZipStream(result, CompressionMode.Compress, leaveOpen: true))
        using (var tar = new TarWriter(zip, leaveOpen: true))
        using (var data = new MemoryStream(Encoding.UTF8.GetBytes(script)))
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "agentspeed-collector") { DataStream = data });
        return result.ToArray();
    }
}
