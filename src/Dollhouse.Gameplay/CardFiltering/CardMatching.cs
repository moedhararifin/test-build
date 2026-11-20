namespace Dollhouse.Gameplay;

internal sealed unsafe partial class NativeGame
{
    internal byte MatchesCardFilters(nint selector, nint card, nint nativeContext)
    {
        ReadOnlySpan<byte> characters = FilterValues(card, CardFilteringBindings.CardCharacters);
        if (!CardSelectionPolicy.ContainsEvery(FilterValues(selector, CardFilteringBindings.SelectedFemales), characters) ||
            !CardSelectionPolicy.ContainsEvery(FilterValues(selector, CardFilteringBindings.SelectedMales), characters) ||
            !CardSelectionPolicy.ContainsEvery(FilterValues(selector, CardFilteringBindings.SelectedOrgans),
                FilterValues(card, CardFilteringBindings.CardOrgans), FilterValues(card, CardFilteringBindings.CardOtherOrgans)) ||
            !CardSelectionPolicy.ContainsEvery(FilterValues(selector, CardFilteringBindings.SelectedConditions),
                FilterValues(card, CardFilteringBindings.CardConditions), FilterValues(card, CardFilteringBindings.CardOtherConditions)))
            return 0;

        // The original ANY checks are implied by ALL. Retain the rest of its
        // filtering and borrowed context without modifying selections/card data.
        return ((delegate* unmanaged<nint, nint, nint, byte>)At(CardFilteringBindings.OriginalCardMatches))
            (selector, card, nativeContext);
    }
}
