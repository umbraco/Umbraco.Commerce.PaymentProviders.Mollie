using Umbraco.Commerce.Core.PaymentProviders;

namespace Umbraco.Commerce.PaymentProviders.Mollie
{
    public class MollieOneTimeSettings
    {
        [PaymentProviderSetting(Name = "Continue URL",
            Description = "The URL to continue to after this provider has done processing. eg: /continue/",
            SortOrder = 100)]
        public string ContinueUrl { get; set; }

        [PaymentProviderSetting(Name = "Cancel URL",
            Description = "The URL to return to if the payment attempt is canceled. eg: /cancel/",
            SortOrder = 200)]
        public string CancelUrl { get; set; }

        [PaymentProviderSetting(Name = "Error URL",
            Description = "The URL to return to if the payment attempt errors. eg: /error/",
            SortOrder = 300)]
        public string ErrorUrl { get; set; }

        [PaymentProviderSetting(Name = "Billing Address (Line 1) Property Alias",
            Description = "The order property alias containing line 1 of the billing address",
            SortOrder = 400)]
        public string BillingAddressLine1PropertyAlias { get; set; }

        [PaymentProviderSetting(Name = "Billing Address City Property Alias",
            Description = "The order property alias containing the city of the billing address",
            SortOrder = 600)]
        public string BillingAddressCityPropertyAlias { get; set; }

        [PaymentProviderSetting(Name = "Billing Address State Property Alias",
            Description = "The order property alias containing the state of the billing address",
            SortOrder = 700)]
        public string BillingAddressStatePropertyAlias { get; set; }

        [PaymentProviderSetting(Name = "Billing Address ZipCode Property Alias",
            Description = "The order property alias containing the zip code of the billing address",
            SortOrder = 800)]
        public string BillingAddressZipCodePropertyAlias { get; set; }

        [PaymentProviderSetting(Name = "Test API Key",
            Description = "Your test Mollie API key",
            SortOrder = 900)]
        public string TestApiKey { get; set; }

        [PaymentProviderSetting(Name = "Live API Key",
            Description = "Your live Mollie API key",
            SortOrder = 1000)]
        public string LiveApiKey { get; set; }

        [PaymentProviderSetting(Name = "Test Mode",
            Description = "Set whether to process payments in test mode.",
            SortOrder = 10000)]
        public bool TestMode { get; set; }

        // Advanced settings

        [PaymentProviderSetting(Name = "Locale",
            Description = "The locale to display the payment provider portal in.",
            IsAdvanced = true,
            SortOrder = 1000100)]
        public string Locale { get; set; }

        [PaymentProviderSetting(Name = "Payment Methods",
            Description = "A comma separated list of payment methods to limit the payment method selection screen by. Can be 'applepay', 'bancontact', 'banktransfer', 'belfius', 'creditcard', 'directdebit', 'eps', 'giftcard', 'giropay', 'ideal', 'kbc', 'klarnapaylater', 'klarnasliceit', 'mybank', 'paypal', 'paysafecard', 'przelewy24', 'sofort' or 'voucher'.",
            IsAdvanced = true,
            SortOrder = 1000200)]
        public string PaymentMethods { get; set; }

        [PaymentProviderSetting(Name = "Order Line Product Type Property Alias",
            Description = "The order line property alias containing a Mollie product type for the order line. Can be either 'physical' or 'digital'.",
            IsAdvanced = true,
            SortOrder = 1000300)]
        public string OrderLineProductTypePropertyAlias { get; set; }

        [PaymentProviderSetting(Name = "Order Line Product Category Property Alias",
            Description = "The order line property alias containing a Mollie product category for the order line. Can be 'meal', 'eco' or 'gift'.",
            IsAdvanced = true,
            SortOrder = 1000300)]
        public string OrderLineProductCategoryPropertyAlias { get; set; }
    }
}
