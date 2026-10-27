using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentSpeed.Core.Remote;

public sealed record RemoteHost(string Id, string Name, string Destination, string Executable = "agentspeed-collector", bool Enabled = true, string? Shell = null, string? Authentication = null)
{
    public bool IsValid => RemoteSettings.IsId(Id) && Id is not ("local" or "all")
        && Name is { Length: > 0 and <= 80 } && !Name.Any(char.IsControl)
        && Destination is { Length: > 0 and <= 255 } && Destination[0] != '-'
        && Destination.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '@' or ':' or '[' or ']')
        && Executable is { Length: > 0 and <= 1024 } && !Executable.Any(char.IsControl)
        && Shell is null or "posix" or "powershell"
        && Authentication is null or "key" or "password";

    public ProcessStartInfo StartInfo(string timeZone, IReadOnlySet<SourceKind>? enabled = null)
    {
        if (!IsValid) throw new ArgumentException("Invalid remote host configuration.");
        var sources = enabled is null ? "" : " --sources " + Quote(string.Join(",", enabled.OrderBy(x => x).Select(x => (int)x)));
        var command = $"{Quote(Executable)} --stream --timezone {Quote(timeZone)}" + sources;
        if (Shell == "powershell")
        {
            static string Ps(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
            var script = $"& {Ps(Executable)} --stream --timezone {Ps(timeZone)}" +
                (enabled is null ? "" : " --sources " + Ps(string.Join(",", enabled.OrderBy(x => x).Select(x => (int)x))));
            command = "powershell.exe -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        }
        return CommandStartInfo(command);
    }

    internal ProcessStartInfo CommandStartInfo(string command)
    {
        if (!IsValid) throw new ArgumentException("Invalid remote host configuration.");
        var start = new ProcessStartInfo(OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh.exe") : "/usr/bin/ssh")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true, CreateNoWindow = true,
        };
        foreach (var argument in new[] { "-T", "-o", Authentication == "password" ? "BatchMode=no" : "BatchMode=yes", "-o", "StrictHostKeyChecking=yes",
            "-o", "ConnectTimeout=10", "-o", "ServerAliveInterval=10", "-o", "ServerAliveCountMax=2", "--", Destination,
            command }) start.ArgumentList.Add(argument);
        if (Authentication == "password")
            foreach (var option in new[] { "PreferredAuthentications=password", "PubkeyAuthentication=no", "KbdInteractiveAuthentication=no", "NumberOfPasswordPrompts=1", "PasswordAuthentication=yes", "ControlMaster=no", "ControlPath=none" })
            { start.ArgumentList.Insert(0, option); start.ArgumentList.Insert(0, "-o"); }
        return start;
    }

    // SSH executes the remote command through the account's shell, not through ArgumentList.
    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}

public sealed record RemoteSettings
{
    [JsonConstructor]
    public RemoteSettings(RemoteHost[]? hosts = null, string selection = "local")
    { Hosts = hosts ?? []; Selection = selection; }

    public RemoteHost[] Hosts { get; init; } = [];
    public string Selection { get; init; } = "local";
    public static RemoteSettings Default { get; } = new();
    public bool Equals(RemoteSettings? other) => ReferenceEquals(this, other) || other is not null
        && Selection == other.Selection && Hosts.SequenceEqual(other.Hosts);
    public override int GetHashCode()
    {
        var hash = new HashCode(); hash.Add(Selection);
        foreach (var host in Hosts) hash.Add(host);
        return hash.ToHashCode();
    }
    public bool IsValid => Hosts is { Length: <= 16 } && Hosts.All(h => h is not null && h.IsValid)
        && Hosts.Select(h => h.Id).Distinct(StringComparer.Ordinal).Count() == Hosts.Length
        && Hosts.Select(h => h.Destination).Distinct(StringComparer.OrdinalIgnoreCase).Count() == Hosts.Length
        && (Selection is "local" or "all" || Hosts.Any(h => h.Id == Selection));
    internal static bool IsId(string value) => value is { Length: > 0 and <= 64 }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public string ToJson() => JsonSerializer.Serialize(this, RemoteJson.Default.RemoteSettings);
    public static RemoteSettings Parse(string json)
    {
        if (json.Length > 64 * 1024) throw new JsonException("Remote settings are too large.");
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || document.RootElement.TryGetProperty("hosts", out var hosts) && hosts.ValueKind == JsonValueKind.Null)
            throw new JsonException("Invalid hosts.");
        var settings = JsonSerializer.Deserialize(json, RemoteJson.Default.RemoteSettings);
        return settings is { IsValid: true } ? settings : throw new JsonException("Invalid hosts or host selection.");
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, MaxDepth = 32)]
[JsonSerializable(typeof(RemoteSettings))]
[JsonSerializable(typeof(RemoteFrame))]
internal partial class RemoteJson : JsonSerializerContext;
