namespace Dollhouse.Gameplay;

internal sealed unsafe partial class NativeGame
{
    private enum FilterVisibility : byte { Visible = 0, Hidden = 2 }

    private void RefreshCardHead(nint widget, HashSet<byte> available, ReadOnlySpan<byte> selected)
    {
        byte character = *(byte*)(widget + CardFilteringBindings.HeadCharacter);
        SetCardAvailability(widget, CardFilteringBindings.HeadEnabled, available.Contains(character));
        int index = selected.IndexOf(character);
        bool isSelected = index >= 0;
        *(byte*)(widget + CardFilteringBindings.HeadSelected) = (byte)(isSelected ? 1 : 0);
        ShowCardFilterControl(widget, CardFilteringBindings.Selection, isSelected);
        if (!isSelected) return;

        bool female = *(byte*)(widget + CardFilteringBindings.HeadIsFemale) != 0;
        ShowCardFilterControl(widget, CardFilteringBindings.FemaleGroup, female);
        ShowCardFilterControl(widget, CardFilteringBindings.MaleGroup, !female);
        SetCardFilterText(*(nint*)(widget + CardFilteringBindings.BadgeText), index == 0 ? "A" : "B");
    }

    private void RefreshCardBody(nint widget, int valueOffset, HashSet<byte> available, ReadOnlySpan<byte> selected)
    {
        byte value = *(byte*)(widget + valueOffset);
        SetCardAvailability(widget, CardFilteringBindings.BodyEnabled, available.Contains(value));
        bool isSelected = selected.Contains(value);
        *(byte*)(widget + CardFilteringBindings.BodySelected) = (byte)(isSelected ? 1 : 0);
        ShowCardFilterControl(widget, CardFilteringBindings.Selection, isSelected);
    }

    private void SetCardAvailability(nint widget, int flag, bool available)
    {
        *(byte*)(widget + flag) = (byte)(available ? 1 : 0);
        ShowCardFilterControl(widget, CardFilteringBindings.EnableIcon, !available);
    }

    private void SetCardFilterSelected(nint widget, bool selected)
    {
        // Use the surviving script event: its colors/button styles belong to the UI.
        *(byte*)(widget + CardFilteringBindings.FilterSelectedFlag) = (byte)(selected ? 1 : 0);
        ((delegate* unmanaged<nint, byte, void>)At(CardFilteringBindings.FilterSelected))(widget, (byte)(selected ? 1 : 0));
    }

    private static void ShowCardFilterControl(nint widget, int member, bool show)
    {
        nint control = *(nint*)(widget + member);
        if (control == 0) return;
        nint method = *(nint*)(*(nint*)control + CardFilteringBindings.VisibilitySlot);
        ((delegate* unmanaged<nint, FilterVisibility, void>)method)
            (control, show ? FilterVisibility.Visible : FilterVisibility.Hidden);
    }

    private void SetCardFilterText(nint textBlock, string value)
    {
        if (textBlock == 0) return;
        var setText = (delegate* unmanaged<nint, CardFilterText*, void>)
            (*(nint*)(*(nint*)textBlock + CardFilteringBindings.TextSlot));
        var fromLiteral = (delegate* unmanaged<CardFilterText*, char*, CardFilterText*>)At(CardFilteringBindings.TextFromLiteral);
        CardFilterText text = default;
        fixed (char* characters = value) fromLiteral(&text, characters);
        // SetText consumes this native by-value FText, including releasing Data.
        // Never Free it as an FString or release its reference a second time.
        setText(textBlock, &text);
    }
}
