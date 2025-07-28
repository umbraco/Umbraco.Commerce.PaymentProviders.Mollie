using Umbraco.Commerce.Core.PaymentProviders;

namespace Umbraco.Commerce.PaymentProviders.Mollie
{
    public class MollieOneTimeSettingsV2 : MollieOneTimeSettings
    {
        /// <summary>
        /// Indicates whether the payment should be captured manually.
        /// </summary>
        [PaymentProviderSetting(
            SortOrder = 1100)]
        public bool ManualCapture { get; set; }
    }
}
