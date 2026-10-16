namespace SmartSolar.Modules.Quotations;

public sealed record QuotationTotals(decimal EquipmentSubtotal, decimal Subtotal, decimal TaxAmount, decimal TotalAmount);

/// <summary>
/// Money rules (VND has no minor unit): every line total and every amount is rounded to whole VND,
/// midpoint away from zero. Subtotal = equipment + labor + transport + other services; tax applies to
/// (subtotal − discount); total = taxable + tax. Client totals are never accepted.
/// </summary>
public static class QuotationPricing
{
    public const string Currency = "VND";
    public const decimal MaxAmount = 999_999_999_999m;

    public static decimal Round(decimal amount) => Math.Round(amount, 0, MidpointRounding.AwayFromZero);

    public static decimal LineTotal(decimal quantity, decimal unitPrice) => Round(quantity * unitPrice);

    public static QuotationTotals Compute(IEnumerable<decimal> lineTotals, decimal labor, decimal transport, decimal other, decimal discount, decimal taxRatePercent)
    {
        var equipment = Round(lineTotals.Sum());
        var subtotal = equipment + Round(labor) + Round(transport) + Round(other);
        var taxable = subtotal - Round(discount);
        var tax = Round(taxable * taxRatePercent / 100m);
        return new QuotationTotals(equipment, subtotal, tax, taxable + tax);
    }
}
