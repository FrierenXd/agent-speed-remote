using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace AgentSpeed.Core.Remote;

/// <summary>SSH_ASKPASS receives a secret over user-only IPC, never through argv, environment or a regular file.</summary>
public sealed class RemotePasswordPrompt : IDisposable
{
    private const string EndpointVariable = "AGENTSPEED_ASKPASS_ENDPOINT";
    private readonly CancellationTokenSource _stop = new();
    private NamedPipeServerStream? _server;
    private string? _directory;
    private int _fifo = -1;
    private readonly TaskCompletionSource<int> _sshPid = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Started(int processId)
    {
        if (_directory is not null) File.WriteAllText(Path.Combine(_directory, "pid"), processId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
        _sshPid.TrySetResult(processId);
    }

    public static RemotePasswordPrompt? Configure(RemoteHost? host, ProcessStartInfo start)
    {
        if (host?.Authentication != "password") return null;
        var password = RemotePasswords.Get(host);
        if (password.Length == 0) throw new IOException("Enter an SSH password and apply the remote host settings first.");
        var prompt = new RemotePasswordPrompt();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var name = "AgentSpeed-askpass-" + Guid.NewGuid().ToString("N");
                prompt._server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                start.Environment[EndpointVariable] = name;
                start.Environment["SSH_ASKPASS"] = Environment.ProcessPath ?? throw new IOException("Cannot locate SSH password helper.");
                _ = prompt.Serve(password);
            }
            else
            {
                // POSIX path is also exercised by the isolated real-sshd integration test.
                prompt._directory = Path.Combine(Path.GetTempPath(), "agentspeed-askpass-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(prompt._directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                var fifo = Path.Combine(prompt._directory, "password");
                if (MkFifo(fifo, 384) != 0) throw new IOException("Cannot create SSH password pipe.");
                prompt._fifo = Open(fifo, 2 | (OperatingSystem.IsMacOS() ? 4 | 16777216 : 2048 | 524288));
                if (prompt._fifo < 0) throw new IOException("Cannot open SSH password pipe.");
                var bytes = Encoding.UTF8.GetBytes(password + "\n");
                try { if (Write(prompt._fifo, bytes, (nuint)bytes.Length) != bytes.Length) throw new IOException("Cannot prepare SSH password pipe."); }
                finally { Array.Clear(bytes); }
                var helper = Path.Combine(prompt._directory, "askpass");
                File.WriteAllText(helper, PosixHelper);
                File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                start.Environment[EndpointVariable] = fifo;
                start.Environment["SSH_ASKPASS"] = helper;
            }
            start.Environment["AGENTSPEED_ASKPASS_MODE"] = "1";
            start.Environment["SSH_ASKPASS_REQUIRE"] = "force";
            start.Environment["DISPLAY"] = "agentspeed:0";
            return prompt;
        }
        catch { prompt.Dispose(); throw; }
    }

    internal const string PosixHelper = "#!/bin/sh\ncase \"$1\" in *[Pp]assphrase*|*[Pp]asscode*) exit 1;; *[Pp]assword*) ;; *) exit 1;; esac\n[ -z \"$SSH_ASKPASS_PROMPT\" ] || exit 1\npidfile=\"${AGENTSPEED_ASKPASS_ENDPOINT%/*}/pid\"\ni=0\nwhile [ ! -s \"$pidfile\" ] && [ \"$i\" -lt 40 ]; do sleep 0.05; i=$((i+1)); done\nIFS= read -r expected < \"$pidfile\" || exit 1\n[ \"$PPID\" = \"$expected\" ] || exit 1\nIFS= read -r secret < \"$AGENTSPEED_ASKPASS_ENDPOINT\" || exit 1\nprintf '%s\\n' \"$secret\"\n";

    private async Task Serve(string password)
    {
        try
        {
            await _server!.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
            var expected = await _sshPid.Task.WaitAsync(_stop.Token).ConfigureAwait(false);
            // A ProxyJump/ProxyCommand child must not receive the destination's password.
            if (OperatingSystem.IsWindows() && (!GetNamedPipeClientProcessId(_server.SafePipeHandle, out var client)
                || ParentPid((int)client) != expected)) return;
            var bytes = Encoding.UTF8.GetBytes(password + "\n");
            try { await _server.WriteAsync(bytes, _stop.Token).ConfigureAwait(false); }
            finally { Array.Clear(bytes); }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException) { }
        finally { _server?.Dispose(); }
    }

    // Called before UI, logging and single-instance setup in the Windows executable.
    public static int? TryRunHelper(string[] args)
    {
        if (Environment.GetEnvironmentVariable("AGENTSPEED_ASKPASS_MODE") != "1") return null;
        if (args.Length != 1 || !args[0].Contains("password", StringComparison.OrdinalIgnoreCase)
            || args[0].Contains("passphrase", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_ASKPASS_PROMPT"))) return 1;
        var endpoint = Environment.GetEnvironmentVariable(EndpointVariable);
        if (endpoint is null || !endpoint.StartsWith("AgentSpeed-askpass-", StringComparison.Ordinal)) return 1;
        try
        {
            using var pipe = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            pipe.Connect(10_000);
            using var output = Console.OpenStandardOutput();
            pipe.CopyTo(output); output.Flush();
            return 0;
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException) { return 1; }
    }

    public void Dispose()
    {
        _stop.Cancel(); _server?.Dispose(); _stop.Dispose();
        if (_fifo >= 0) { Close(_fifo); _fifo = -1; }
        if (_directory is not null) { try { Directory.Delete(_directory, true); } catch (IOException) { } }
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)] private static extern int MkFifo(string path, uint mode);
    [DllImport("libc", EntryPoint = "open", SetLastError = true)] private static extern int Open(string path, int flags);
    [DllImport("libc", EntryPoint = "write", SetLastError = true)] private static extern nint Write(int fd, byte[] buffer, nuint count);
    [DllImport("libc", EntryPoint = "close")] private static extern int Close(int fd);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr process, int kind, ref ProcessBasicInformation info, int size, out int returned);
    [StructLayout(LayoutKind.Sequential)] private struct ProcessBasicInformation
    {
        public IntPtr Reserved1, Peb, Reserved2, Reserved3, ProcessId, ParentId;
    }
    private static int ParentPid(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            var info = new ProcessBasicInformation();
            return NtQueryInformationProcess(process.Handle, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _) == 0
                ? (int)info.ParentId : -1;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return -1; }
    }
}
