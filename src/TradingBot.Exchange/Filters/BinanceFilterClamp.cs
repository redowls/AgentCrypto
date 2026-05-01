using TradingBot.Core.Domain;
using TradingBot.Exchange.Abstractions;

namespace TradingBot.Exchange.Filters;

/// PRICE_FILTER / LOT_SIZE / MIN_NOTIONAL clamping. Mirrors Binance.Net's
/// BinanceHelpers.ClampPrice / ClampQuantity but is duplicated here so the
/// behaviour is unit-tested against our own contract — Binance.Net's helper
/// API has changed between major versions and we want to insulate the rest
/// of the codebase from that drift.
public static class BinanceFilterClamp
{
    /// Floor <paramref name="price"/> to the nearest multiple of
    /// <paramref name="tickSize"/>. Throws if tickSize is non-positive.
    public static decimal ClampPriceToTick(decimal price, decimal tickSize)
    {
        if (tickSize <= 0m)
            throw new ArgumentOutOfRangeException(nameof(tickSize), tickSize, "tickSize must be > 0");
        if (price <= 0m)
            throw new ArgumentOutOfRangeException(nameof(price), price, "price must be > 0");

        var steps = Math.Floor(price / tickSize);
        return decimal.Round(steps * tickSize, CountDecimals(tickSize), MidpointRounding.ToZero);
    }

    /// Floor <paramref name="quantity"/> to the nearest multiple of
    /// <paramref name="stepSize"/>. Throws if stepSize is non-positive.
    public static decimal ClampQuantityToStep(decimal quantity, decimal stepSize)
    {
        if (stepSize <= 0m)
            throw new ArgumentOutOfRangeException(nameof(stepSize), stepSize, "stepSize must be > 0");
        if (quantity < 0m)
            throw new ArgumentOutOfRangeException(nameof(quantity), quantity, "quantity must be >= 0");

        var steps = Math.Floor(quantity / stepSize);
        return decimal.Round(steps * stepSize, CountDecimals(stepSize), MidpointRounding.ToZero);
    }

    /// If qty * price is below the exchange minimum notional, throws
    /// <see cref="MinNotionalViolatedException"/>. Otherwise returns the
    /// (already step-clamped) qty unchanged.
    public static decimal EnforceMinNotional(decimal quantity, decimal price, decimal minNotional)
    {
        if (minNotional < 0m)
            throw new ArgumentOutOfRangeException(nameof(minNotional));

        var notional = quantity * price;
        if (notional < minNotional)
            throw new MinNotionalViolatedException(quantity, price, minNotional);

        return quantity;
    }

    /// Convenience: clamp a (price, qty) pair to the supplied filter and verify
    /// minNotional in one call. Used by the gateway just before submitting.
    public static (decimal Price, decimal Quantity) ClampForLimit(
        decimal price,
        decimal quantity,
        Symbol filter)
    {
        var p = ClampPriceToTick(price, filter.TickSize);
        var q = ClampQuantityToStep(quantity, filter.StepSize);
        EnforceMinNotional(q, p, filter.MinNotional);
        return (p, q);
    }

    /// Convenience for market orders (qty * lastPrice notional check).
    public static decimal ClampForMarket(decimal quantity, decimal referencePrice, Symbol filter)
    {
        var q = ClampQuantityToStep(quantity, filter.StepSize);
        EnforceMinNotional(q, referencePrice, filter.MinNotional);
        return q;
    }

    private static int CountDecimals(decimal value)
    {
        // tickSize / stepSize are exchange-supplied positive decimals like 0.01.
        // Use the scale of the supplied value so 0.0001 yields 4. We must walk
        // bits because decimal.GetBits is the only invariant accessor for scale.
        var bits = decimal.GetBits(value);
        return (bits[3] >> 16) & 0x7F;
    }
}

public sealed class MinNotionalViolatedException : Exception
{
    public MinNotionalViolatedException(decimal quantity, decimal price, decimal minNotional)
        : base($"Order notional {quantity * price:F8} below min notional {minNotional:F8} (qty={quantity}, px={price}).")
    {
        Quantity = quantity;
        Price = price;
        MinNotional = minNotional;
    }

    public decimal Quantity { get; }
    public decimal Price { get; }
    public decimal MinNotional { get; }
}
