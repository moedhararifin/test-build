namespace Dollhouse.Gameplay;

internal static class CardAreaPolicy
{
    // HM_EnumMoodLocation and RoomType values from the supported binary's enum
    // registrations. Floor/wall are intentionally portable, even with area tags.
    internal const byte Floor = 0, Wall = 1, Prop = 2, AllAreas = 1;

    private enum RoomType : byte
    {
        None, Room_ALL, Room_Club, Room_CircleRoom, Room_Dormitory,
        Room_NeoInnsmouth, Room_NeoArkham, Room_NeoDunwich, Room_NeoRhodeIsland, Room_Pool,
        VR_Room_Club, VR_Room_CircleRoom, VR_Room_Dormitory,
        VR_Room_NeoInnsmouth, VR_Room_NeoArkham, VR_Room_NeoDunwich, VR_Room_NeoRhodeIsland, VR_Room_Pool
    }

    internal static bool HasAreaRestriction(byte location, ReadOnlySpan<byte> areas) =>
        location == Prop && !areas.IsEmpty && !areas.Contains(AllAreas);

    internal static bool TryGetArea(ReadOnlySpan<char> mapName, out byte area)
    {
        const string prefix = "RoomType::";
        if (mapName.StartsWith(prefix, StringComparison.Ordinal)) mapName = mapName[prefix.Length..];
        bool valid = Enum.TryParse<RoomType>(mapName, true, out var room) && Enum.IsDefined(room) && (byte)room >= 2;
        area = valid ? (byte)room : (byte)0;
        return valid;
    }
}
