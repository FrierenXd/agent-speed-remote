using System.Collections.Concurrent;

namespace AgentSpeed.Core.Remote;

/// <summary>Session-only credentials, scoped to both the row and its SSH destination.</summary>
public static class RemotePasswords
{
    private static readonly ConcurrentDictionary<(string Id, string Destination), string> Values = new();
    private static readonly ConcurrentDictionary<(string Id, string Destination), long> Versions = new();
    private static long _revision;
    public static long Revision => Interlocked.Read(ref _revision);
    public static string Get(RemoteHost host) => Values.GetValueOrDefault((host.Id, host.Destination)) ?? "";
    public static long RevisionFor(RemoteHost host) => host.Authentication == "password" ? Versions.GetValueOrDefault((host.Id, host.Destination)) : 0;
    public static void Set(RemoteHost host, string password)
    {
        if (password.Length > 1024 || password.Any(c => c is '\0' or '\r' or '\n'))
            throw new ArgumentException("The SSH password must be a single line of at most 1024 characters.");
        if (password.Length == 0) Values.TryRemove((host.Id, host.Destination), out _);
        else Values[(host.Id, host.Destination)] = password;
        Versions[(host.Id, host.Destination)] = Interlocked.Increment(ref _revision);
    }
    public static void Retain(IEnumerable<RemoteHost> hosts)
    {
        var keep = hosts.Where(h => h.Authentication == "password").Select(h => (h.Id, h.Destination)).ToHashSet();
        foreach (var key in Values.Keys) if (!keep.Contains(key)) Values.TryRemove(key, out _);
        Interlocked.Increment(ref _revision);
    }
}
