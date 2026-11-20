using System.Runtime.InteropServices;

namespace Dollhouse.Gameplay;

internal sealed unsafe partial class NativeGame
{
    [StructLayout(LayoutKind.Explicit, Size = 8)]
    private struct WeakObject
    {
        [FieldOffset(0)] public int Index;
        [FieldOffset(4)] public int Serial;
        internal readonly ulong Key => (uint)Index | ((ulong)(uint)Serial << 32);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectArray { public nint Data; public int Count, Capacity; }
    private sealed class BlendTransition(WeakObject instance, int offset, double before)
    {
        internal readonly WeakObject Instance = instance;
        internal readonly int Offset = offset;
        internal readonly double Before = before;
        internal double LastWritten = before;
        internal BulgeGrowth? Growth;
    }
    private sealed class ExperimentGrowth
    {
        internal WeakObject World, Room, Performer;
        internal int PerformerOffset, CardOffset, CardId;
        internal readonly List<BlendTransition> Blends = [];
    }
    private readonly List<ExperimentGrowth> experimentGrowth = [];

    private WeakObject WeakReference(nint instance)
    {
        WeakObject result = default;
        ((delegate* unmanaged<WeakObject*, nint*, void>)At(SupportedBuild.AssignWeakObject))(&result, &instance);
        return result;
    }
    private nint ResolveWeak(WeakObject reference) =>
        ((delegate* unmanaged<WeakObject*, nint>)At(SupportedBuild.GetWeakObject))(&reference);

    private ExperimentGrowth? CaptureExperimentBulge(nint context, nint engine, nint world, nint room, nint performer, nint performerType, int performerOffset)
    {
        if (!TryScriptMember(performerType, "AnimInstanceList", 16, out var animations) ||
            !TryScriptMember(performerType, "CardID", 4, out var card) || card.TypeId != 4)
        { LogCumflation("experiment growth metadata missing: AnimInstanceList/CardID"); return null; }
        nint arrayType = ((delegate* unmanaged<nint, int, nint>)At(SupportedBuild.ScriptTypeById))(engine, animations.TypeId);
        if (arrayType == 0 || ScriptString(((delegate* unmanaged<nint, nint>)At(SupportedBuild.ScriptTypeName))(arrayType)) != "TArray")
        { LogCumflation("experiment growth metadata rejected: animation array type"); return null; }
        nint element = ((delegate* unmanaged<nint, uint, nint>)At(SupportedBuild.ScriptSubtype))(arrayType, 0);
        int elementId = ((delegate* unmanaged<nint, uint, int>)At(SupportedBuild.ScriptSubtypeId))(arrayType, 0);
        string elementName = element == 0 ? "" : ScriptString(((delegate* unmanaged<nint, nint>)At(SupportedBuild.ScriptTypeName))(element));
        bool objectElements = elementName is "UObject" or "AActor" or "A_BaseAnimCharacter" or "A_CharacterFemale";
        if (!objectElements || (elementId & 0x40000000) == 0)
        { LogCumflation($"experiment growth metadata rejected: array element={elementName}, type={elementId:x}"); return null; }
        ObjectArray array = *(ObjectArray*)(performer + animations.Offset);
        if (array.Count < 0 || array.Count > array.Capacity || (array.Count > 0 && array.Data == 0))
        { LogCumflation($"experiment growth array rejected: count={array.Count}, capacity={array.Capacity}, data={array.Data != 0}"); return null; }
        var result = new ExperimentGrowth
        {
            World = WeakReference(world), Room = WeakReference(room), Performer = WeakReference(performer),
            PerformerOffset = performerOffset, CardOffset = card.Offset, CardId = *(int*)(performer + card.Offset)
        };
        // Same three animation families handled by the game's SetBlendForBulge utility.
        var families = new (string Module, string Type)[]
        {
            ("AMBP.Character.AMBP_Character", "U_AMBP_Character"),
            ("AMBP.ALS.ALS_FemaleAnimInstance", "U_ALS_FemaleAnimInstance"),
            ("AMBP.Monster.AMBP_Monster", "U_AMBP_Monseter")
        };
        nint characterType = FindScriptType(engine, "Actor.Character.Base.CharacterFemale", "A_CharacterFemale");
        if (characterType == 0) return null;
        nint characterClass = NativeClassForScriptType(characterType);
        BodyGetter getter = FindBodyGetter(engine, characterType);
        if (getter.Function == 0) return null;
        // Despite its name, AnimInstanceList contains character actors. Follow the
        // same female cast, virtual getter and mesh accessor as UpdateCharacterBulgeSize.
        var instances = new HashSet<nint>();
        for (int i = 0; i < array.Count; i++)
        {
            nint actor = ((nint*)array.Data)[i];
            bool valid = IsNativeInstance(actor, characterClass);
            if (!valid) continue;
            nint instance = ReadBodyAnimation(context, actor, getter);
            if (instance != 0) instances.Add(instance);
        }
        var seen = new HashSet<nint>();
        foreach (var family in families)
        {
            nint type = FindScriptType(engine, family.Module, family.Type);
            if (type == 0) continue;
            if (!TryScriptMember(type, "BlendForBulge", 8, out var blend) || blend.TypeId != 11)
            { LogCumflation($"experiment growth family={family.Type}: double BlendForBulge unavailable"); continue; }
            nint nativeClass = NativeClassForScriptType(type);
            foreach (nint instance in instances)
            {
                if (instance == 0 || seen.Contains(instance) || !IsNativeInstance(instance, nativeClass)) continue;
                double before = *(double*)(instance + blend.Offset);
                if (!double.IsFinite(before) || before < 0 || before > 1) continue;
                seen.Add(instance);
                result.Blends.Add(new(WeakReference(instance), blend.Offset, before));
            }
        }
        return result;
    }

    private void BeginExperimentBulgeGrowth(ExperimentGrowth? snapshot)
    {
        if (snapshot == null) return; // Keep the native refresh if validated visual metadata is unavailable.
        experimentGrowth.RemoveAll(x => x.Performer.Key == snapshot.Performer.Key);
        for (int i = snapshot.Blends.Count - 1; i >= 0; i--)
        {
            var blend = snapshot.Blends[i];
            nint instance = ResolveWeak(blend.Instance);
            if (instance == 0) { snapshot.Blends.RemoveAt(i); continue; }
            double target = *(double*)(instance + blend.Offset);
            if (!double.IsFinite(target) || target > 1 || target <= blend.Before)
            { snapshot.Blends.RemoveAt(i); continue; }
            blend.Growth = new(blend.Before, target);
            *(double*)(instance + blend.Offset) = blend.Before;
        }
        if (snapshot.Blends.Count > 0) experimentGrowth.Add(snapshot);
    }

    internal void TickCumflationVisuals(nint ticker, float seconds)
    {
        // Preserve all original frame work even when no transition exists. Native frame loop,
        // same thread as gameplay: no worker-thread UObject writes, timer polling or recursion.
        ((delegate* unmanaged<nint, float, void>)At(SupportedBuild.FrameTickerTick))(ticker, seconds);
        if (experimentGrowth.Count == 0 || !float.IsFinite(seconds) || seconds <= 0) return;
        nint world = *(nint*)At(SupportedBuild.CurrentWorld);
        for (int i = experimentGrowth.Count - 1; i >= 0; i--)
        {
            var group = experimentGrowth[i];
            if (world == 0 || ResolveWeak(group.World) != world)
            { experimentGrowth.RemoveAt(i); continue; }
            nint room = ResolveWeak(group.Room), performer = ResolveWeak(group.Performer);
            if (room == 0 || performer == 0 ||
                *(nint*)(room + group.PerformerOffset) != performer || *(int*)(performer + group.CardOffset) != group.CardId)
            { experimentGrowth.RemoveAt(i); continue; }
            for (int j = group.Blends.Count - 1; j >= 0; j--)
            {
                var blend = group.Blends[j];
                nint instance = ResolveWeak(blend.Instance);
                // An intervening native animation or reinitialization takes ownership immediately.
                double current = instance == 0 ? double.NaN : *(double*)(instance + blend.Offset);
                if (!double.IsFinite(current) || Math.Abs(current - blend.LastWritten) > 1e-9)
                { group.Blends.RemoveAt(j); continue; }
                blend.LastWritten = blend.Growth!.Advance(seconds);
                *(double*)(instance + blend.Offset) = blend.LastWritten;
                if (blend.Growth.Complete)
                    group.Blends.RemoveAt(j);
            }
            if (group.Blends.Count == 0)
                experimentGrowth.RemoveAt(i);
        }
    }

    internal void CancelExperimentBulgeGrowth()
    {
        experimentGrowth.Clear();
    }
}
