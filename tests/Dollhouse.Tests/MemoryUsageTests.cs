using System.Reflection;
using System.Security.Cryptography;
using Dollhouse.Core.Patching;
using Dollhouse.Core.Startup;
using Dollhouse.Server.Debugging;
using Dollhouse.Server.Echo;
using Dollhouse.Server.Endpoints;
using Dollhouse.Server.Requests;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Dollhouse.Tests;

internal static class MemoryUsageTests
{
    internal static async Task VerifyAsync()
    {
        BinaryProgressTests.Verify();
        VerifyBinaryLifecycle();
        VerifyResourceExtraction();
        await VerifyRequestAllocations();
    }

    private static long Allocated(Action action)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static void VerifyBinaryLifecycle()
    {
        if (GameBinaryFixture.Original is not { } fixture) return;
        using var workspace = new TemporaryWorkspace();
        var target = Path.Combine(workspace.Paths.Root, "game.exe");
        var backup = GameBinary.BackupPath(target);
        File.Copy(fixture, target);
        File.WriteAllText(target + ".bak", "manual copy must stay untouched");
        long fresh = Allocated(() => GameBinary.PrepareOriginal(target));
        Check.True(fresh < 8 * 1024 * 1024, "Fresh preparation allocated an EXE-sized buffer.");
        Check.True(!File.Exists(backup), "Fresh preparation created a backup.");
        long repeat = Allocated(() => GameBinary.PrepareOriginal(target));
        Check.True(repeat < 8 * 1024 * 1024, "Repeat preparation allocated an EXE-sized buffer.");
        var completed = new List<BinaryProgress>();
        void Progress(BinaryProgress update) { if (update.IsComplete) completed.Add(update); }
        using (var verified = GameBinary.PrepareAndOpenOriginal(target, Progress))
        {
            Check.Equal("Verifying installed game", completed.Single().Operation);
            Check.Equal(0L, verified.Position);
            Check.Throws<IOException>(() => { using var writer = File.OpenWrite(target); });
        }

        File.Copy(fixture, backup);
        File.WriteAllText(target, "arbitrary old modifications; no patch recipe is needed");
        completed.Clear();
        long restore = Allocated(() => GameBinary.PrepareOriginal(target, Progress));
        Check.True(restore < 16 * 1024 * 1024, "Restoration loaded a whole image.");
        Check.True(!File.Exists(backup), "Restoration retained its redundant backup.");
        Check.Equal(GameBuild.OriginalSha256, Hash(target));
        Check.Equal("manual copy must stay untouched", File.ReadAllText(target + ".bak"));
        Check.True(completed.Select(x => x.Operation).SequenceEqual(new[] {
            "Verifying previous-version backup", "Verifying installed game", "Restoring original game",
            "Verifying restored copy", "Verifying restored game", "Removing redundant binary backup"
        }), "Restoration must distinguish each verified file and publication stage.");

        File.Copy(fixture, backup);
        completed.Clear();
        GameBinary.PrepareOriginal(target, Progress); // Already-original target consumes duplicate backup too.
        Check.True(completed.Select(x => x.Operation).SequenceEqual(new[] {
            "Verifying previous-version backup", "Verifying installed game", "Removing redundant binary backup"
        }), "Already-original target was hashed a second time during backup removal.");
        Check.True(!File.Exists(backup), "Duplicate backup was retained.");
        File.WriteAllText(backup, "invalid source");
        Check.Throws<InvalidDataException>(() => GameBinary.PrepareOriginal(target));
        Check.Equal("invalid source", File.ReadAllText(backup));
        Check.Equal(GameBuild.OriginalSha256, Hash(target));
        File.Delete(backup);
        File.WriteAllText(target, "modified without a backup");
        Check.Throws<InvalidDataException>(() => GameBinary.PrepareOriginal(target));
        Check.Equal("modified without a backup", File.ReadAllText(target));
        File.Copy(fixture, backup);
        using (File.Open(target, FileMode.Open, FileAccess.Read, FileShare.None))
            Check.Throws<IOException>(() => GameBinary.PrepareOriginal(target));
        Check.Equal(GameBuild.OriginalSha256, Hash(backup));
        Check.True(!Directory.EnumerateFiles(workspace.Paths.Root, "*.tmp").Any(), "Failed restoration left temporary files.");
        Check.Throws<InvalidDataException>(() => GameBinary.PrepareOriginal(target, update =>
        {
            if (update.Operation == "Restoring original game" && update.IsComplete)
            {
                var copy = Directory.EnumerateFiles(workspace.Paths.Root, "*.tmp").Single();
                using var damaged = File.OpenWrite(copy); damaged.WriteByte(0); // Only an owned disposable copy.
            }
        }));
        Check.Equal("modified without a backup", File.ReadAllText(target));
        Check.Equal(GameBuild.OriginalSha256, Hash(backup));
        Check.True(!Directory.EnumerateFiles(workspace.Paths.Root, "*.tmp").Any(), "Unverified copy survived failure.");
        GameBinary.PrepareOriginal(target); // Retry remains supported.

        using var original = GameBinary.OpenVerifiedOriginal(target);
        long plan = Allocated(() => LauncherLoadPlan.Create(original, "dhruntime.dll", 0x60000000));
        Check.True(plan < 2 * 1024 * 1024, "Launcher metadata parsing loaded the image.");
        Console.WriteLine($"Memory allocations: fresh={fresh:N0}, repeat={repeat:N0}, restore={restore:N0}, launch-plan={plan:N0} bytes.");
    }

    private static void VerifyResourceExtraction()
    {
        var assembly = Assembly.Load("Dollhouse");
        if (!assembly.GetManifestResourceNames().Contains("Dollhouse.Runtime")) return;
        using var workspace = new TemporaryWorkspace();
        var path = Path.Combine(workspace.Paths.Root, "runtime.dll");
        long extract = Allocated(() => Check.True(BundledFile.Extract("Dollhouse.Runtime", path, assembly), "Runtime not extracted."));
        long compare = Allocated(() => Check.True(!BundledFile.Extract("Dollhouse.Runtime", path, assembly), "Unchanged runtime rewritten."));
        Check.True(extract < 2 * 1024 * 1024 && compare < 2 * 1024 * 1024, "Runtime extraction/comparison buffered the DLL.");
        File.WriteAllText(path, "changed runtime");
        Check.True(BundledFile.Extract("Dollhouse.Runtime", path, assembly), "Changed runtime not repaired.");
        Console.WriteLine($"Memory allocations: runtime extract={extract:N0}, compare={compare:N0} bytes.");
    }

    private static async Task VerifyRequestAllocations()
    {
        using var services = new ServiceCollection().AddSingleton(new ServerPipeline(new QuietJournal(), new NoRequestCapture(),
            [new ClientReportEndpoint().Route])).BuildServiceProvider();
        await Send(services, []); // Warm the metadata-only request path.
        byte[] payload = new byte[RequestDispatcher.MaximumBodySize];
        long before = GC.GetTotalAllocatedBytes(true);
        await Send(services, payload);
        long allocated = GC.GetTotalAllocatedBytes(true) - before;
        Check.True(allocated < 24 * 1024 * 1024, "An API request allocated eager Base64/text or a duplicate body array.");
        Console.WriteLine($"Memory allocations: 8 MiB API request={allocated:N0} bytes (no listener).");
    }

    private static async Task Send(IServiceProvider services, byte[] bytes)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = "POST"; context.Request.Path = "/api/v1/client/report";
        context.Request.Body = new MemoryStream(bytes, false);
        context.Response.Body = new MemoryStream();
        await RequestDispatcher.HandleAsync(context);
        Check.Equal(200, context.Response.StatusCode); Check.Equal(0L, context.Response.Body.Length);
    }

    private static string Hash(string path)
    { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    private sealed class QuietJournal : IRequestJournal { public void Write(RequestMetadata metadata) { } }
}
