using Dollhouse.Core.Patching.Native;

namespace Dollhouse.Core.Patching;

internal static class CameraPatchRecipe
{
    internal static void AddTo(NativePatchPlan patch) =>
        // Replace the destructive metadata-discard call with installation of one
        // script implementation. Metadata stays alive for subsequent parser lookup.
        // The caller still destroys its temporary precompiled-data object normally.
        patch.RedirectCall("Install the direct cinematic preset loader and retain script metadata",
            SupportedBuild.DiscardScriptMetadataCall, SupportedBuild.DiscardScriptMetadata,
            patch.Import(GameplayLibrary.InstallCameraLoader));
}
