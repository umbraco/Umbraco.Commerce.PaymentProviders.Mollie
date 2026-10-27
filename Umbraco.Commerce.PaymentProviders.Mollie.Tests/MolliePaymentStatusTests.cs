using System.Globalization;
using Mollie.Api.Models;
using Mollie.Api.Models.Payment;
using Mollie.Api.Models.Payment.Response;
using NUnit.Framework;
using MolliePaymentStatus = Mollie.Api.Models.Payment.PaymentStatus;
using PaymentStatus = Umbraco.Commerce.Core.Models.PaymentStatus;

namespace Umbraco.Commerce.PaymentProviders.Mollie.Tests
{
    public class MolliePaymentStatusTests
    {
        private static Amount Eur(string value) => new("EUR", value);

        private static PaymentResponse Payment(string? refunded, string? captured, string amount = "100.00", string status = MolliePaymentStatus.Paid)
            => new()
            {
                Resource = "payment",
                Id = "tr_test",
                Mode = Mode.Test,
                CreatedAt = DateTime.UtcNow,
                ProfileId = "pfl_test",
                SequenceType = SequenceType.OneOff,
                Status = status,
                Amount = Eur(amount),
                AmountRefunded = refunded == null ? null : Eur(refunded),
                AmountCaptured = captured == null ? null : Eur(captured),
            };

        [TestCase("25.00")]
        [TestCase("50.00")]
        [TestCase("99.99")]
        public void Partial_Refund_Of_Captured_Amount_Is_PartiallyRefunded(string refunded)
            => Assert.That(MollieOneTimePaymentProviderV2.GetPaymentStatus(Payment(refunded, "100.00")), Is.EqualTo(PaymentStatus.PartiallyRefunded));

        [TestCase("100.00")]
        [TestCase("120.00")]
        public void Refund_Reaching_Captured_Amount_Is_Refunded(string refunded)
            => Assert.That(MollieOneTimePaymentProviderV2.GetPaymentStatus(Payment(refunded, "100.00")), Is.EqualTo(PaymentStatus.Refunded));

        [Test]
        public void Partial_Refund_Is_Measured_Against_Captured_Not_Payment_Amount()
            => Assert.That(MollieOneTimePaymentProviderV2.GetPaymentStatus(Payment("60.00", "80.00", amount: "100.00")), Is.EqualTo(PaymentStatus.PartiallyRefunded));

        [Test]
        public void Refund_Equal_To_Captured_Below_Payment_Amount_Is_Refunded()
            => Assert.That(MollieOneTimePaymentProviderV2.GetPaymentStatus(Payment("80.00", "80.00", amount: "100.00")), Is.EqualTo(PaymentStatus.Refunded));

        [TestCase(null, "50.00", PaymentStatus.PartiallyRefunded)]
        [TestCase(null, "100.00", PaymentStatus.Refunded)]
        [TestCase("0.00", "50.00", PaymentStatus.PartiallyRefunded)]
        public void Missing_Or_Zero_AmountCaptured_Falls_Back_To_Amount(string? captured, string refunded, PaymentStatus expected)
            => Assert.That(MollieOneTimePaymentProviderV2.GetPaymentStatus(Payment(refunded, captured)), Is.EqualTo(expected));

        [TestCase(MolliePaymentStatus.Paid, PaymentStatus.Captured)]
        [TestCase(MolliePaymentStatus.Authorized, PaymentStatus.Authorized)]
        [TestCase(MolliePaymentStatus.Canceled, PaymentStatus.Cancelled)]
        [TestCase(MolliePaymentStatus.Expired, PaymentStatus.Cancelled)]
        [TestCase(MolliePaymentStatus.Failed, PaymentStatus.Error)]
        [TestCase(MolliePaymentStatus.Open, PaymentStatus.PendingExternalSystem)]
        public void Without_Refund_Status_Is_Mapped_From_Mollie_Status(string mollieStatus, PaymentStatus expected)
        {
            Assert.That(MollieOneTimePaymentProviderV2.GetPaymentStatus(Payment(null, "100.00", status: mollieStatus)), Is.EqualTo(expected));
            Assert.That(MollieOneTimePaymentProviderV2.GetPaymentStatus(Payment("0.00", "100.00", status: mollieStatus)), Is.EqualTo(expected));
        }

        [Test]
        public void ParseMollieAmount_Is_Culture_Invariant()
        {
            CultureInfo original = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("da-DK");
                Assert.That(MollieOneTimePaymentProviderV2.ParseMollieAmount(Eur("1234.50")), Is.EqualTo(1234.50m));
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        }

        [Test]
        public void ParseMollieAmount_Returns_Zero_For_Missing_Value()
        {
            Assert.That(MollieOneTimePaymentProviderV2.ParseMollieAmount(null), Is.EqualTo(0m));
            Assert.That(MollieOneTimePaymentProviderV2.ParseMollieAmount(new Amount("EUR", string.Empty)), Is.EqualTo(0m));
        }
    }
}
