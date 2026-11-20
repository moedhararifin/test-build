using Dollhouse.Core;

namespace Dollhouse.Tests;

internal sealed class TemporaryWorkspace : IDisposable
{
    private readonly string allowedParent;
    public WorkspacePaths Paths { get; }

    public TemporaryWorkspace()
    {
        var repository = WorkspacePaths.Discover();
        allowedParent = Path.GetFullPath(repository.Local) + Path.DirectorySeparatorChar;
        Paths = new(Path.Combine(repository.Local, "dotnet-tests-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Paths.Root);
    }

    public void Dispose()
    {
        var resolved = Path.GetFullPath(Paths.Root);
        if (!resolved.StartsWith(allowedParent, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(resolved).StartsWith("dotnet-tests-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unsafe test cleanup path.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }
}
