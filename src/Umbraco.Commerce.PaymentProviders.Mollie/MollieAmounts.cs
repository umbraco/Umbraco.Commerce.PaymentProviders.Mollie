using System;
using System.Collections.Generic;
using MollieAmount = Mollie.Api.Models.Amount;

namespace Umbraco.Commerce.PaymentProviders.Mollie
{
    /// <summary>
    /// Builds the amounts and VAT rates sent to Mollie.
    /// </summary>
    /// <remarks>
    /// Mollie.Api 4 rounded every amount to the currency's decimals before sending it. Mollie.Api 5
    /// sends the value as set and Mollie rejects one with too many decimals, so the rounding is done
    /// here to keep the v4 behaviour.
    /// </remarks>
    internal static class MollieAmounts
    {
        // Mollie.Api's own list of currencies that don't use 2 decimals.
        private static readonly Dictionary<string, int> CurrencyDecimals = new(StringComparer.OrdinalIgnoreCase)
        {
            { "JPY", 0 },
            { "ISK", 0 },
        };

        public static MollieAmount Create(string currencyCode, decimal value)
        {
            int decimals = CurrencyDecimals.TryGetValue(currencyCode, out int d) ? d : 2;
            return new MollieAmount(currencyCode, Math.Round(value, decimals, MidpointRounding.AwayFromZero));
        }

        /// <summary>
        /// Converts a tax rate fraction (0.21) to the percentage Mollie expects, with 2 decimals (21.00).
        /// </summary>
        public static decimal ToVatRate(decimal taxRate)
            => Math.Round(taxRate * 100m, 2, MidpointRounding.AwayFromZero) + 0.00m;
    }
}
