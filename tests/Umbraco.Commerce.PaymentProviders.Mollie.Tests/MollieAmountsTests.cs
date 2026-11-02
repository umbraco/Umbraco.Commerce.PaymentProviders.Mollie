using System.Globalization;
using NUnit.Framework;

namespace Umbraco.Commerce.PaymentProviders.Mollie.Tests
{
    public class MollieAmountsTests
    {
        [TestCase("EUR", "10.005", "10.01")]
        [TestCase("EUR", "-10.005", "-10.01")]
        [TestCase("EUR", "10.004", "10.00")]
        [TestCase("EUR", "20", "20")]
        [TestCase("eur", "1.235", "1.24")]
        [TestCase("JPY", "500.5", "501")]
        [TestCase("ISK", "499.49", "499")]
        public void Create_Rounds_To_The_Currency_Decimals_Like_Mollie_Api_4(string currency, string value, string expected)
        {
            var amount = MollieAmounts.Create(currency, decimal.Parse(value, CultureInfo.InvariantCulture));

            Assert.That(amount.Currency, Is.EqualTo(currency));
            Assert.That(amount.Value, Is.EqualTo(decimal.Parse(expected, CultureInfo.InvariantCulture)));
        }

        [TestCase("0.21", "21.00")]
        [TestCase("0.2", "20.00")]
        [TestCase("0", "0.00")]
        [TestCase("0.0525", "5.25")]
        [TestCase("0.123456", "12.35")]
        public void ToVatRate_Is_A_Percentage_With_Two_Decimals(string taxRate, string expected)
        {
            decimal vatRate = MollieAmounts.ToVatRate(decimal.Parse(taxRate, CultureInfo.InvariantCulture));

            Assert.That(vatRate.ToString(CultureInfo.InvariantCulture), Is.EqualTo(expected));
        }

        [Test]
        public void ToVatRate_Of_A_Ratio_Derived_Rate_Is_Rounded()
        {
            // Adjustment lines derive their rate from WithTax / WithoutTax, which can have many decimals.
            decimal taxRate = (12.10m / 10.00m) - 1;

            Assert.That(MollieAmounts.ToVatRate(taxRate).ToString(CultureInfo.InvariantCulture), Is.EqualTo("21.00"));
        }
    }
}
