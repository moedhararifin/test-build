using Dollhouse.Core.Patching.Native;
using static Iced.Intel.Register;

namespace Dollhouse.Core.Patching;

internal static class CardFilteringPatchRecipe
{
    internal static void AddTo(NativePatchPlan patch)
    {
        patch.RedirectCall("Require every selected character, experiment and category on the same card",
            CardFilteringBindings.MatchCardCall, CardFilteringBindings.OriginalCardMatches,
            patch.Import(GameplayLibrary.MatchesCardFilters));

        var cardLock = patch.Import(GameplayLibrary.SetCardLock);
        patch.RedirectToLeaf("Restore prop-only card area locks through the existing member relay",
            CardFilteringBindings.CardLockRelay,
            expect: code => code.Jump(CardFilteringBindings.OriginalCardLock),
            implementation: code => code.Jump(cardLock));

        patch.ReplaceBlock("Refresh left-side card choices from the native matching-card array",
            CardFilteringBindings.RefreshTail, CardFilteringBindings.RefreshTailEnd,
            expect: code =>
            {
                code.CompareDwordOnStack(CardFilteringBindings.MatchingCardsOnStack + 8, 0);
                code.MoveFrom(RCX, R14);
                code.SetEqual(DL);
                code.Call(CardFilteringBindings.ShowNoFounded);
            },
            replacement: code =>
            {
                // The callback submits the original no-results event, then applies the
                // Steam availability pass. Native filtering, cleanup and unwind stay intact.
                code.Move(RCX, R14);
                code.AddressOnStack(RDX, CardFilteringBindings.MatchingCardsOnStack);
                code.Call(patch.Import(GameplayLibrary.RefreshCardFilters));
            });
    }
}
