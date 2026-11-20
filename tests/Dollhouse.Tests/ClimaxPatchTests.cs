using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using Dollhouse.App;
using Dollhouse.Core.Patching;
using Dollhouse.Gameplay;
using Iced.Intel;
using SupportedBuild = Dollhouse.Gameplay.SupportedBuild;

namespace Dollhouse.Tests;

internal static unsafe class ClimaxPatchTests
{
    private static int level, reads, frees;
    private static nint expectedRole;

    public static void Verify()
    {
        foreach (var (rank, threshold) in new[] { (-1, 90f), (0, 90f), (4, 90f), (5, 80f), (6, 80f), (10, 80f) })
            Check.Equal(threshold, ClimaxPatch.FemaleEcstasyThreshold(rank));
        Check.Throws<ArgumentException>(() => AppOptions.Parse(["--game-patch", "simultaneous-climax"]));
        Check.Throws<ArgumentException>(() => AppOptions.Parse(["--game-patch", "simultaneous-climax", "--game-patch", "simultaneous-climax"]));
        Check.Throws<ArgumentException>(() => AppOptions.Parse(["--clean", "--game-patch", "simultaneous-climax"]));
        VerifyAbi();
        if (Environment.GetEnvironmentVariable("DOLLHOUSE_TEST_GAMEPLAY") is { } path)
        {
            nint library = NativeLibrary.Load(Path.GetFullPath(path)); // Our own library only; never load game code.
            foreach (string name in new[] { "DollhouseFemaleEcstasyThreshold", "DollhouseFemaleThresholdFunction",
                "DollhouseGenericFemaleThreshold", "DollhouseExecFemaleThreshold" })
                Check.True(NativeLibrary.GetExport(library, name) != 0, "Missing climax export: " + name);
            nint function = 0;
            ((delegate* unmanaged<nint*, void>)NativeLibrary.GetExport(library, "DollhouseFemaleThresholdFunction"))(&function);
            Check.Equal(NativeLibrary.GetExport(library, "DollhouseFemaleEcstasyThreshold"), function);
            Check.Equal(90f, ((delegate* unmanaged<nint, GameString*, float>)function)(0, null));
            nint* arguments = stackalloc nint[2]; arguments[0] = 0; arguments[1] = 0;
            float result = -1;
            ((delegate* unmanaged<nint, nint*, float*, void>)NativeLibrary.GetExport(library, "DollhouseGenericFemaleThreshold"))(0, arguments, &result);
            Check.Equal(90f, result);
            GameString emptyRole = default;
            byte* frame = stackalloc byte[0x100];
            *(nint*)(frame + 0x20) = 0x1234;
            ((delegate* unmanaged<GameString*, nint, float*, void>)NativeLibrary.GetExport(library, "DollhouseExecFemaleThreshold"))(&emptyRole, (nint)frame, &result);
            Check.Equal(90f, result);
            Check.Equal((nint)0x1235, *(nint*)(frame + 0x20));
        }
    }

    private static void VerifyAbi()
    {
        var game = new NativeGame(0, rva => rva switch
        {
            SupportedBuild.RoleFavor => (nint)(delegate* unmanaged<GameString*, int*, int*, void>)&ReadFavor,
            SupportedBuild.Free => (nint)(delegate* unmanaged<void*, void>)&Free,
            _ => throw new InvalidOperationException("Unexpected native call.")
        });
        char* name = stackalloc char[5] { 'T', 'e', 's', 't', '\0' };
        var role = new GameString { Data = name, Length = 5, Capacity = 5 };
        expectedRole = (nint)(&role); reads = frees = 0;
        byte* frame = stackalloc byte[0x100];
        foreach (int rank in new[] { 0, 4, 5, 6 })
        {
            level = rank;
            Check.Equal(rank >= 5 ? 80f : 90f, game.FemaleEcstasyThreshold(&role));
            foreach (nint code in new nint[] { 0, 0x1234 })
            {
                *(nint*)(frame + 0x20) = code;
                float result = -1;
                game.CompleteThresholdExec(&role, (nint)frame, &result);
                Check.Equal(rank >= 5 ? 80f : 90f, result);
                Check.Equal(code == 0 ? 0 : code + 1, *(nint*)(frame + 0x20));
            }
        }
        Check.Equal(12, reads); Check.Equal(8, frees);
        Check.Equal(90f, game.FemaleEcstasyThreshold(null));
        Check.Equal(12, reads);
    }

    [UnmanagedCallersOnly]
    private static void ReadFavor(GameString* role, int* experience, int* rank)
    {
        if ((nint)role != expectedRole || role->Data[0] != 'T') Environment.FailFast("Role ABI mismatch.");
        reads++; *experience = 999; *rank = level;
    }
    [UnmanagedCallersOnly]
    private static void Free(void* value) { if (value != ((GameString*)expectedRole)->Data) Environment.FailFast("Free ABI mismatch."); frees++; }

}
