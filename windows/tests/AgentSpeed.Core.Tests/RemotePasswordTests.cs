using System.Diagnostics;
using AgentSpeed.Core.Remote;

namespace AgentSpeed.Core.Tests;

public sealed class RemotePasswordTests
{
    [Fact]
    public void PasswordsNeverEnterSettingsArgumentsOrEnvironment()
    {
        var host = Host();
        const string secret = "space ' quote $ non-ascii 密码";
        RemotePasswords.Set(host, secret);
        try
        {
            var settings = new RemoteSettings([host]);
            Assert.Equal(host, Assert.Single(RemoteSettings.Parse(settings.ToJson()).Hosts));
            Assert.DoesNotContain(secret, settings.ToJson());
            var start = host.StartInfo("UTC");
            using var prompt = RemotePasswordPrompt.Configure(host, start);
            Assert.Contains("BatchMode=no", start.ArgumentList);
            Assert.Contains("StrictHostKeyChecking=yes", start.ArgumentList);
            Assert.Contains("PreferredAuthentications=password", start.ArgumentList);
            Assert.Contains("ControlPath=none", start.ArgumentList);
            Assert.DoesNotContain(secret, string.Join(" ", start.ArgumentList));
            Assert.DoesNotContain(secret, string.Join(" ", start.Environment.Values));
            Assert.Equal("force", start.Environment["SSH_ASKPASS_REQUIRE"]);
            Assert.Equal("", RemotePasswords.Get(host with { Destination = "another-host" }));
        }
        finally { RemotePasswords.Set(host, ""); }
    }

    [Fact]
    public void LegacyHostsKeepNoninteractiveKeyAuthentication()
    {
        var host = Host() with { Authentication = null };
        var start = host.StartInfo("UTC");
        Assert.Contains("BatchMode=yes", start.ArgumentList);
        Assert.Null(RemotePasswordPrompt.Configure(host, start));
        Assert.False((host with { Authentication = "otp" }).IsValid);
        Assert.Throws<ArgumentException>(() => RemotePasswords.Set(host, "not\na password"));
    }

    [Fact]
    public async Task MissingPasswordDoesNotStartSSHOrRetry()
    {
        var host = Host();
        var statuses = new List<string>();
        await RemoteConnection.RunCore(() => new ProcessStartInfo("not-an-executable"), "UTC", _ => { }, statuses.Add,
            CancellationToken.None, host);
        Assert.Equal(new[] { "connecting", "passwordRequired" }, statuses);
    }

    [Fact]
    public async Task PasswordLoginFailurePausesAutomaticRetries()
    {
        if (OperatingSystem.IsWindows()) return;
        var host = Host(); RemotePasswords.Set(host, "bad password");
        try
        {
            var attempts = 0; var statuses = new List<string>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await RemoteConnection.RunCore(() =>
            {
                attempts++;
                var start = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
                start.ArgumentList.Add("-c"); start.ArgumentList.Add("exit 255"); return start;
            }, "UTC", _ => { }, statuses.Add, timeout.Token, host);
            Assert.Equal(1, attempts);
            Assert.Contains("authenticationFailed", statuses);
        }
        finally { RemotePasswords.Set(host, ""); }
    }

    [Fact]
    public async Task PosixAskpassUsesPrivateFifoAndRejectsJumpHostPrompts()
    {
        if (OperatingSystem.IsWindows()) return;
        var host = Host(); const string secret = "test ' $ \\ password";
        RemotePasswords.Set(host, secret);
        string? directory = null;
        try
        {
            var start = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("-c"); start.ArgumentList.Add("\"$SSH_ASKPASS\" \"user@server's password: \"");
            using (var authentication = RemotePasswordPrompt.Configure(host, start))
            {
                directory = Path.GetDirectoryName(start.Environment["SSH_ASKPASS"])!;
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
                Assert.DoesNotContain(secret, File.ReadAllText(start.Environment["SSH_ASKPASS"]!));
                using var process = Process.Start(start)!;
                // The helper is a child of this shell, matching the system SSH helper topology.
                authentication!.Started(process.Id);
                Assert.Equal(secret + "\n", await process.StandardOutput.ReadToEndAsync());
                await process.WaitForExitAsync(); Assert.Equal(0, process.ExitCode);
                authentication.Started(Environment.ProcessId);
                using var jump = Process.Start(start)!;
                Assert.Equal("", await jump.StandardOutput.ReadToEndAsync());
                await jump.WaitForExitAsync(); Assert.NotEqual(0, jump.ExitCode);
            }
            Assert.False(Directory.Exists(directory));
        }
        finally { RemotePasswords.Set(host, ""); }
    }

    private static RemoteHost Host() => new(Guid.NewGuid().ToString("N"), "Password test", "user@server", Authentication: "password");
}
