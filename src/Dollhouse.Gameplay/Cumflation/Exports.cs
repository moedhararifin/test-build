using System.Runtime.InteropServices;

namespace Dollhouse.Gameplay;

public static unsafe partial class Exports
{
    [UnmanagedCallersOnly(EntryPoint = GameplayLibrary.DispatchBulgeEligibility)]
    internal static void DispatchBulgeEligibility(nint context, nint function)
    { try { Game.DispatchBulgeEligibility(context, function); } catch (Exception error) { Report(error); } }

    [UnmanagedCallersOnly(EntryPoint = GameplayLibrary.SemenCapacity)]
    internal static int ReadSemenCapacity(GameString* role)
    {
        try { return SemenCapacity.Read(role); }
        catch (Exception error) { Report(error); return SupportedBuild.SemenCapacityMl; }
    }

    [UnmanagedCallersOnly(EntryPoint = GameplayLibrary.CaptureDrain)]
    internal static void CaptureDrain(GameString* role, int attribute, int value)
    { try { Game.CaptureDrainAndWrite(role, attribute, value); } catch (Exception error) { Report(error); } }

    [UnmanagedCallersOnly(EntryPoint = GameplayLibrary.DispatchDrainCleared)]
    internal static void DispatchDrainCleared(nint nativeDelegate, GameString* role)
    { try { Game.DispatchDrainCleared(nativeDelegate, role); } catch (Exception error) { Report(error); } }

    [UnmanagedCallersOnly(EntryPoint = GameplayLibrary.ShowDrainRightMsg)]
    internal static void ShowDrainRightMsg(nint subsystem, byte type, GameString* player, GameString* playerId, GameString* message, GameString* number)
    { try { Game.ShowDrainRightMsg(subsystem, type, player, playerId, message, number); } catch (Exception error) { Report(error); } }

    [UnmanagedCallersOnly(EntryPoint = GameplayLibrary.TickCumflation)]
    internal static void TickCumflation(nint ticker, float deltaSeconds)
    {
        try { Game.TickCumflationVisuals(ticker, deltaSeconds); }
        catch (Exception error) { Report(error); }
    }

    // Binding adapters supply playerId, borrowed changes and the native world context.
    // The original native wrapper still destroys its temporary request after this returns.
    [UnmanagedCallersOnly(EntryPoint = GameplayLibrary.PersistCumflationWithContext)]
    internal static void PersistCumflationWithContext(int playerId, RolePropertyChanges* changes, nint context)
    {
        if (changes == null) return;
        try { Game.HandleRolePropertyChanges(playerId, changes, context); }
        catch (Exception error) { Report(error); }
    }

    [UnmanagedCallersOnly(EntryPoint = GameplayLibrary.NotifyCumflationCleared)]
    internal static void NotifyCumflationCleared(nint localDelegate, LocalRoleAttributesChanged* notification)
    {
        try { Game.NotifyVolumesCleared(localDelegate, notification); }
        catch (Exception error) { Report(error); }
    }
}
