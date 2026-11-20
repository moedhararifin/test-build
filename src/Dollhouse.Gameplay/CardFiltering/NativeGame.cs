namespace Dollhouse.Gameplay;

internal sealed unsafe partial class NativeGame
{
    internal void RefreshCardFilters(nint selector, CardFilterArray<nint>* matchingCards)
    {
        // Preserve the original reflected notification, including the zero-results case.
        ((delegate* unmanaged<nint, byte, void>)At(CardFilteringBindings.ShowNoFounded))
            (selector, (byte)(matchingCards->Count == 0 ? 1 : 0));

        var available = new CardFilterAvailability();
        foreach (nint card in matchingCards->Span)
        {
            if (card == 0) throw new InvalidDataException("Null matching card metadata.");
            CardFilterAvailability.Include(available.Characters, FilterValues(card, CardFilteringBindings.CardCharacters));
            CardFilterAvailability.Include(available.Organs, FilterValues(card, CardFilteringBindings.CardOrgans));
            CardFilterAvailability.Include(available.Organs, FilterValues(card, CardFilteringBindings.CardOtherOrgans));
            CardFilterAvailability.Include(available.Conditions, FilterValues(card, CardFilteringBindings.CardConditions));
            CardFilterAvailability.Include(available.Conditions, FilterValues(card, CardFilteringBindings.CardOtherConditions));
        }

        // Finish collecting before touching widgets. No card pointers or native arrays
        // survive this synchronous callback; native code retains all matching-card ownership.
        nint headClass = CardFilterClass(CardFilteringBindings.HeadClass);
        var females = *(CardFilterArray<byte>*)(selector + CardFilteringBindings.SelectedFemales);
        var males = *(CardFilterArray<byte>*)(selector + CardFilteringBindings.SelectedMales);
        RefreshCardFilterPanel(selector, CardFilteringBindings.FemalePanel, headClass,
            widget => RefreshCardHead(widget, available.Characters, females.Span));
        RefreshCardFilterPanel(selector, CardFilteringBindings.MalePanel, headClass,
            widget => RefreshCardHead(widget, available.Characters, males.Span));

        nint bodyClass = CardFilterClass(CardFilteringBindings.BodyClass);
        var organs = *(CardFilterArray<byte>*)(selector + CardFilteringBindings.SelectedOrgans);
        var conditions = *(CardFilterArray<byte>*)(selector + CardFilteringBindings.SelectedConditions);
        RefreshCardFilterPanel(selector, CardFilteringBindings.OrganPanel, bodyClass,
            widget => RefreshCardBody(widget, CardFilteringBindings.OrganValue, available.Organs, organs.Span));
        RefreshCardFilterPanel(selector, CardFilteringBindings.ConditionPanel, bodyClass,
            widget => RefreshCardBody(widget, CardFilteringBindings.ConditionValue, available.Conditions, conditions.Span));

        nint filterClass = CardFilterClass(CardFilteringBindings.FilterClass);
        byte state = *(byte*)(selector + CardFilteringBindings.SelectedState);
        byte smile = *(byte*)(selector + CardFilteringBindings.SelectedSmile);
        RefreshCardFilterPanel(selector, CardFilteringBindings.StatePanel, filterClass,
            widget => SetCardFilterSelected(widget, *(byte*)(widget + CardFilteringBindings.FilterState) == state));
        RefreshCardFilterPanel(selector, CardFilteringBindings.SmilePanel, filterClass,
            widget => SetCardFilterSelected(widget, *(byte*)(widget + CardFilteringBindings.FilterSmile) == smile));

        int rare = *(int*)(selector + CardFilteringBindings.SelectedRare);
        RefreshCardFilterPanel(selector, CardFilteringBindings.RarePanel, CardFilterClass(CardFilteringBindings.RareClass), widget =>
        {
            bool selected = *(int*)(widget + CardFilteringBindings.RareValue) == rare;
            *(byte*)(widget + CardFilteringBindings.RareSelectedFlag) = (byte)(selected ? 1 : 0);
            ((delegate* unmanaged<nint, byte, void>)At(CardFilteringBindings.RareSelected))(widget, (byte)(selected ? 1 : 0));
        });
    }

    private static ReadOnlySpan<byte> FilterValues(nint owner, int offset) =>
        ((CardFilterArray<byte>*)(owner + offset))->Span;

    private nint CardFilterClass(int getter) => ((delegate* unmanaged<nint>)At(getter))();

    private void RefreshCardFilterPanel(nint selector, int panelOffset, nint widgetClass, Action<nint> refresh)
    {
        nint panel = *(nint*)(selector + panelOffset);
        if (panel == 0) return; // Native selector variants may omit a panel.
        CardFilterArray<nint> children = default;
        try
        {
            ((delegate* unmanaged<nint, CardFilterArray<nint>*, nint>)At(CardFilteringBindings.PanelChildren))(panel, &children);
            foreach (nint widget in children.Span)
                if (IsNativeInstance(widget, widgetClass)) refresh(widget);
        }
        finally
        {
            // GetAllChildren returns its own TArray, even when empty or a child is foreign.
            if (children.Data != null) ((delegate* unmanaged<void*, void>)At(SupportedBuild.Free))(children.Data);
        }
    }
}
