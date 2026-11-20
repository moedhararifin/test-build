#if DOLLHOUSE_PATCH_BINDINGS
namespace Dollhouse.Core.Patching;
#else
namespace Dollhouse.Gameplay;
#endif

internal static class CameraBindings
{
    public const string PathVariable = "DOLLHOUSE_CAMERA_CONFIG";
    public const string FileName = "CamConf.config";
    public const int MaximumBytes = 32 * 1024 * 1024;
}
