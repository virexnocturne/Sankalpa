namespace Blocker.Core;

public interface IClientIdentity
{
    string? Name { get; }
    string? Sid { get; }
    bool IsAuthenticated { get; }
    bool IsAdministrator { get; }
    bool IsLocalSystem { get; }
}

public class ClientIdentity : IClientIdentity
{
    public string? Name { get; init; }
    public string? Sid { get; init; }
    public bool IsAuthenticated { get; init; }
    public bool IsAdministrator { get; init; }
    public bool IsLocalSystem { get; init; }

    public ClientIdentity() { }

    public ClientIdentity(string? name, string? sid, bool isAuthenticated, bool isAdministrator, bool isLocalSystem)
    {
        Name = name;
        Sid = sid;
        IsAuthenticated = isAuthenticated;
        IsAdministrator = isAdministrator;
        IsLocalSystem = isLocalSystem;
    }

    public static IClientIdentity Anonymous { get; } = new ClientIdentity
    {
        Name = "Anonymous",
        Sid = null,
        IsAuthenticated = false,
        IsAdministrator = false,
        IsLocalSystem = false
    };

    public static IClientIdentity LocalSystem { get; } = new ClientIdentity
    {
        Name = "NT AUTHORITY\\SYSTEM",
        Sid = "S-1-5-18",
        IsAuthenticated = true,
        IsAdministrator = true,
        IsLocalSystem = true
    };

    public static IClientIdentity Administrator(string? name = null, string? sid = null) => new ClientIdentity
    {
        Name = name ?? "Administrator",
        Sid = sid ?? "S-1-5-32-544",
        IsAuthenticated = true,
        IsAdministrator = true,
        IsLocalSystem = false
    };

    public static IClientIdentity StandardUser(string? name = null, string? sid = null) => new ClientIdentity
    {
        Name = name ?? "StandardUser",
        Sid = sid ?? "S-1-5-21-standard-user",
        IsAuthenticated = true,
        IsAdministrator = false,
        IsLocalSystem = false
    };

    public static IClientIdentity CreateLocalSystem(string? name = null, string? sid = null) => new ClientIdentity
    {
        Name = name ?? "NT AUTHORITY\\SYSTEM",
        Sid = sid ?? "S-1-5-18",
        IsAuthenticated = true,
        IsAdministrator = true,
        IsLocalSystem = true
    };
}
