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

    /// <summary>
    /// Product of <c>1 - amount / prior close</c> for dividends going ex strictly after
    /// <paramref name="barDate"/>. <paramref name="closeOnDayBefore"/> supplies the close
    /// on the last trading day before an ex-date, and returns null when that bar is not
    /// available; such a dividend contributes nothing rather than a guessed factor.
    /// </summary>
    public static decimal DividendFactor(
        IEnumerable<CorporateAction> actions,
        DateOnly barDate,
        Func<DateOnly, decimal?> closeOnDayBefore)
    {
        var factor = 1m;

        foreach (var action in actions)
        {
            if (action.ActionType != CorporateActionType.Dividend ||
                action.ExDate <= barDate ||
                action.Amount is not { } amount ||
                amount <= 0m)
            {
                continue;
            }

            if (closeOnDayBefore(action.ExDate) is not { } priorClose || priorClose <= 0m)
            {
                continue;
            }

            factor *= 1m - (amount / priorClose);
        }

        return factor;
    }

    /// <summary>The factor for one bar under a given adjustment mode.</summary>
    public static decimal Factor(
        IEnumerable<CorporateAction> actions,
        DateOnly barDate,
        PriceAdjustment adjustment,
        Func<DateOnly, decimal?> closeOnDayBefore) => adjustment switch
        {
            PriceAdjustment.None => 1m,
            PriceAdjustment.SplitsOnly => SplitFactor(actions, barDate),
            PriceAdjustment.SplitsAndDividends =>
                SplitFactor(actions, barDate) * DividendFactor(actions, barDate, closeOnDayBefore),
            _ => throw new ArgumentOutOfRangeException(nameof(adjustment), adjustment, null)
        };
}
