namespace AgentSpeed.Core.Remote;

/// <summary>Session-only credentials, scoped to both the row and its SSH destination.</summary>
public static class RemotePasswords
{
    private static readonly object Gate = new();
    private static readonly Dictionary<(string Id, string Destination), string> Values = new();
    private static readonly Dictionary<(string Id, string Destination), long> Versions = new();
    private static long _revision;
    public static long Revision { get { lock (Gate) return _revision; } }
    public static string Get(RemoteHost host) { lock (Gate) return Values.GetValueOrDefault((host.Id, host.Destination)) ?? ""; }
    public static long RevisionFor(RemoteHost host) { lock (Gate) return host.Authentication == "password" ? Versions.GetValueOrDefault((host.Id, host.Destination)) : 0; }
    public static void Set(RemoteHost host, string password)
    {
        // OpenSSH's askpass response buffer is 1024 bytes, including newline and NUL.
        if (System.Text.Encoding.UTF8.GetByteCount(password) > 1022 || password.Any(c => c is '\0' or '\r' or '\n'))
            throw new ArgumentException("The SSH password must be a single line of at most 1022 UTF-8 bytes.");
        lock (Gate)
        {
            if (password.Length == 0) Values.Remove((host.Id, host.Destination));
            else Values[(host.Id, host.Destination)] = password;
            Versions[(host.Id, host.Destination)] = ++_revision;
        }
    }
    public static void Retain(IEnumerable<RemoteHost> hosts)
    {
        var keep = hosts.Where(h => h.Authentication == "password").Select(h => (h.Id, h.Destination)).ToHashSet();
        lock (Gate)
        {
            foreach (var key in Values.Keys.ToArray()) if (!keep.Contains(key)) Values.Remove(key);
            ++_revision;
        }
    }
}
