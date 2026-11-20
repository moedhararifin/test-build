namespace Dollhouse.Core;

public sealed record WorkspacePaths(string Root, string LocalDirectoryName = ".local")
{
    public const string GameStateDirectoryName = ".dollhouse";
    public static WorkspacePaths ForGame(string root) => new(Path.GetFullPath(root), GameStateDirectoryName);
    public string Local => Path.Combine(Root, LocalDirectoryName);
    public string Tls => Path.Combine(Local, "tls");
    public string Certificate => Path.Combine(Tls, "server.crt");
    public string PrivateKey => Path.Combine(Tls, "server.key");

    public static WorkspacePaths Discover(string? explicitRoot = null)
    {
        if (explicitRoot is not null)
        {
            var fullPath = Path.GetFullPath(explicitRoot);
            if (!Directory.Exists(fullPath))
                throw new DirectoryNotFoundException(fullPath);
            return new(fullPath);
        }

        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "Dollhouse.slnx")))
                    return new(directory.FullName);

        throw new InvalidOperationException("Cannot find Dollhouse.slnx. Supply --root <repository>.");
    }
}
