namespace MarketData.Domain;

/// <summary>
/// Cumulative corporate-action factors. Pure: no clock, no I/O, no prior state, so the
/// same inputs give the same factor forever. Callers supply only the actions they consider
/// visible; deciding visibility is the query layer's job, not this one's.
/// </summary>
public static class AdjustmentCalculator
{
    /// <summary>
    /// Product of split ratios for splits taking effect strictly after
    /// <paramref name="barDate"/>. On an ex-date the quoted price already reflects the
    /// split, so that day's own split must not be applied again.
    /// </summary>
    public static decimal SplitFactor(IEnumerable<CorporateAction> actions, DateOnly barDate)
    {
        var factor = 1m;

        foreach (var action in actions)
        {
            if (action.ActionType != CorporateActionType.Split ||
                action.ExDate <= barDate ||
                action.Ratio is not { } ratio ||
                ratio <= 0m)
            {
                continue;
            }

            factor *= ratio;
        }

        return factor;
    }
}
