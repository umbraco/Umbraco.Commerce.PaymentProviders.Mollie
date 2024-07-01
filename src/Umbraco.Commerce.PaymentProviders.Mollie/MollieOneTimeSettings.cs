using Umbraco.Commerce.Core.PaymentProviders;

namespace Umbraco.Commerce.PaymentProviders.Mollie
{
    public class MollieOneTimeSettings
    {
        [PaymentProviderSetting(
            SortOrder = 100)]
        public string ContinueUrl { get; set; }

        [PaymentProviderSetting(
            SortOrder = 200)]
        public string CancelUrl { get; set; }

        [PaymentProviderSetting(
            SortOrder = 300)]
        public string ErrorUrl { get; set; }

        [PaymentProviderSetting(
            SortOrder = 400)]
        public string BillingAddressLine1PropertyAlias { get; set; }

        [PaymentProviderSetting(
            SortOrder = 600)]
        public string BillingAddressCityPropertyAlias { get; set; } = "billingAddressLine1";

        [PaymentProviderSetting(
            SortOrder = 700)]
        public string BillingAddressStatePropertyAlias { get; set; }

        [PaymentProviderSetting(
            SortOrder = 800)]
        public string BillingAddressZipCodePropertyAlias { get; set; }

        [PaymentProviderSetting(
            SortOrder = 900)]
        public string TestApiKey { get; set; }

        [PaymentProviderSetting(
            SortOrder = 1000)]
        public string LiveApiKey { get; set; }

        [PaymentProviderSetting(
            SortOrder = 10000)]
        public bool TestMode { get; set; }

        // Advanced settings

        [PaymentProviderSetting(
            IsAdvanced = true,
            SortOrder = 1000100)]
        public string Locale { get; set; }

        [PaymentProviderSetting(
            IsAdvanced = true,
            SortOrder = 1000200)]
        public string PaymentMethods { get; set; }

        [PaymentProviderSetting(
            IsAdvanced = true,
            SortOrder = 1000300)]
        public string OrderLineProductTypePropertyAlias { get; set; }

        [PaymentProviderSetting(
            IsAdvanced = true,
            SortOrder = 1000300)]
        public string OrderLineProductCategoryPropertyAlias { get; set; }
    }
}
