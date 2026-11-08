namespace EgyChina.Pos.Core;

/// <summary>
/// Quick cash tender suggestions from Egyptian note denominations (5, 10, 50, 100, 200 EGP).
/// Example for 606.00: 605 (floor 5), 610 (ceil 10, default), 650, 700, 800.
/// </summary>
public static class CashSuggestions
{
    /// <summary>Ceil steps in piasters (10 / 50 / 100 / 200 EGP). Floor-to-5 is separate.</summary>
    private static readonly long[] CeilStepsMinor = [1_000, 5_000, 10_000, 20_000];

    public static long CeilToPound(long totalMinor)
    {
        if (totalMinor <= 0)
            return 0;
        if (totalMinor % 100 == 0)
            return totalMinor;
        return checked(((totalMinor / 100) + 1) * 100);
    }

    public static long FloorToStep(long amountMinor, long stepMinor)
    {
        if (stepMinor <= 0)
            throw new ArgumentOutOfRangeException(nameof(stepMinor));
        if (amountMinor <= 0)
            return 0;
        return checked((amountMinor / stepMinor) * stepMinor);
    }

    public static long CeilToStep(long amountMinor, long stepMinor)
    {
        if (stepMinor <= 0)
            throw new ArgumentOutOfRangeException(nameof(stepMinor));
        if (amountMinor <= 0)
            return stepMinor;
        return checked(((amountMinor + stepMinor - 1) / stepMinor) * stepMinor);
    }

    /// <summary>Default tender: next 10 EGP note boundary (most realistic handed cash).</summary>
    public static long DefaultAmount(long totalMinor)
    {
        var baseMinor = CeilToPound(totalMinor);
        if (baseMinor <= 0)
            return 0;
        return CeilToStep(baseMinor, 1_000);
    }

    /// <summary>
    /// Unique ascending cash options: floor-to-5, ceil-to-10/50/100/200.
    /// </summary>
    public static IReadOnlyList<long> Suggest(long totalMinor)
    {
        if (totalMinor <= 0)
            return Array.Empty<long>();

        var baseMinor = CeilToPound(totalMinor);
        var set = new SortedSet<long>();

        var floor5 = FloorToStep(totalMinor, 500);
        if (floor5 > 0)
            set.Add(floor5);

        foreach (var step in CeilStepsMinor)
            set.Add(CeilToStep(baseMinor, step));

        return set.ToList();
    }
}
