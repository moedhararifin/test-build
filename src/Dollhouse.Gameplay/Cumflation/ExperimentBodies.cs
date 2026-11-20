namespace Dollhouse.Gameplay;

internal sealed unsafe partial class NativeGame
{
    private readonly record struct BodyGetter(nint Function, nint MeshClass);

    private BodyGetter FindBodyGetter(nint engine, nint characterType)
    {
        nint named;
        fixed (byte* name = "GetMainBodyPart\0"u8)
            named = ((delegate* unmanaged<nint, byte*, nint>)At(SupportedBuild.ScriptMethodByName))(characterType, name);
        if (named != 0 && Validate(named, out var direct)) return direct;

        LogCumflation("experiment body getter unavailable on A_CharacterFemale");
        return default;

        bool Validate(nint function, out BodyGetter result)
        {
            result = default;
            string name = ScriptString(((delegate* unmanaged<nint, nint>)At(SupportedBuild.ScriptFunctionName))(function));
            string owner = ScriptString(((delegate* unmanaged<nint, nint>)At(SupportedBuild.ScriptFunctionObjectName))(function));
            uint parameters = ((delegate* unmanaged<nint, uint>)At(SupportedBuild.ScriptParameterCount))(function);
            uint modifiers = 0;
            int id = ((delegate* unmanaged<nint, uint*, int>)At(SupportedBuild.ScriptReturnType))(function, &modifiers);
            nint type = ((delegate* unmanaged<nint, int, nint>)At(SupportedBuild.ScriptTypeById))(engine, id);
            string typeName = type == 0 ? "<missing>" : ScriptString(((delegate* unmanaged<nint, nint>)At(SupportedBuild.ScriptTypeName))(type));
            // Native UObject signatures can use implicit references. A handle bit alone
            // neither establishes nor rules out a mesh return; validate its actual type.
            bool meshType = typeName is "USkeletalMeshComponent" or "UPHSkeletalMeshComponent";
            nint nativeClass = meshType ? NativeClassForScriptType(type) : 0;
            bool valid = name == "GetMainBodyPart" && owner == "A_CharacterFemale" && parameters == 0 && nativeClass != 0;
            if (!valid) LogCumflation($"body getter signature rejected: owner={owner}, name={name}, parameters={parameters}, returnType={id:x}, modifiers={modifiers:x}, returnName={typeName}");
            if (valid) result = new(function, nativeClass);
            return valid;
        }
    }

    private nint ReadBodyAnimation(nint context, nint actor, BodyGetter getter)
    {
        if (!PrepareScriptCall(context, getter.Function, actor, "GetMainBodyPart") ||
            !ExecuteScriptCall(context, "GetMainBodyPart")) return 0;
        nint mesh = ((delegate* unmanaged<nint, nint>)At(SupportedBuild.ScriptReturnObject))(context);
        bool valid = IsNativeInstance(mesh, getter.MeshClass);
        if (!valid) { LogCumflation("body mesh absent or wrong class"); return 0; }
        nint animation = ((delegate* unmanaged<nint, nint>)At(SupportedBuild.MeshAnimInstance))(mesh);
        return animation;
    }
}
