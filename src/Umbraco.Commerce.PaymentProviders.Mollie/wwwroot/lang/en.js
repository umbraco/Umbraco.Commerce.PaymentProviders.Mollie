export default {
  ucPaymentProviders: {
    mollieOnetimeLabel: 'Mollie (One Time)',
    mollieOnetimeDescription: 'Mollie payment provider for one time payments',
    mollieOnetimeSettingsContinueUrlLabel: 'Continue URL',
    mollieOnetimeSettingsContinueUrlDescription: 'The URL to continue to after this provider has done processing. eg: /continue/',
    mollieOnetimeSettingsCancelUrlLabel: 'Cancel URL',
    mollieOnetimeSettingsCancelUrlDescription: 'The URL to return to if the payment attempt is canceled. eg: /cart/',
    mollieOnetimeSettingsErrorUrlLabel: 'Error URL',
    mollieOnetimeSettingsErrorUrlDescription: 'The URL to return to if the payment attempt errors. eg: /error/',

    mollieOnetimeSettingsBillingAddressLine1PropertyAliasLabel: 'Billing Address (Line 1) Property Alias',
    mollieOnetimeSettingsBillingAddressLine1PropertyAliasDescription: '[Required] The order property alias containing line 1 of the billing address',

    mollieOnetimeSettingsBillingAddressCityPropertyAliasLabel: 'Billing Address City Property Alias',
    mollieOnetimeSettingsBillingAddressCityPropertyAliasDescription: '[Required] The order property alias containing the city of the billing address',

    mollieOnetimeSettingsBillingAddressStatePropertyAliasLabel: 'Billing Address State Property Alias',
    mollieOnetimeSettingsBillingAddressStatePropertyAliasDescription: 'The order property alias containing the state of the billing address',

    mollieOnetimeSettingsBillingAddressZipCodePropertyAliasLabel: 'Billing Address ZipCode Property Alias',
    mollieOnetimeSettingsBillingAddressZipCodePropertyAliasDescription: '[Required] The order property alias containing the zip code of the billing address',

    mollieOnetimeSettingsTestApiKeyLabel: 'Test API Key',
    mollieOnetimeSettingsTestApiKeyDescription: 'Your test Mollie API key',

    mollieOnetimeSettingsLiveApiKeyLabel: 'Live API Key',
    mollieOnetimeSettingsLiveApiKeyDescription: 'Your live Mollie API key',

    mollieOnetimeSettingsTestModeLabel: 'Test Mode',
    mollieOnetimeSettingsTestModeDescription: 'Set whether to process payments in test mode',

    mollieOnetimeSettingsLocaleLabel: 'Locale',
    mollieOnetimeSettingsLocaleDescription: 'The locale to display the payment provider portal in',

    mollieOnetimeSettingsPaymentMethodsLabel: 'Payment Methods',
    mollieOnetimeSettingsPaymentMethodsDescription: "A comma separated list of payment methods to limit the payment method selection screen by. Can be 'applepay', 'bancontact', 'banktransfer', 'belfius', 'creditcard', 'directdebit', 'eps', 'giftcard', 'giropay', 'ideal', 'kbc', 'klarnapaylater', 'klarnasliceit', mybank', 'paypal', 'paysafecard', 'przelewy24', 'sofort' or 'voucher'",

    mollieOnetimeSettingsOrderLineProductTypePropertyAliasLabel: 'Order Line Product Type Property Alias',
    mollieOnetimeSettingsOrderLineProductTypePropertyAliasDescription: "The order line property alias containing a Mollie product type for the order line. Can be either 'physical' or 'digital'",

    mollieOnetimeSettingsOrderLineProductCategoryPropertyAliasLabel: 'Order Line Product Category Property Alias',
    mollieOnetimeSettingsOrderLineProductCategoryPropertyAliasDescription: "The order line property alias containing a Mollie product category for the order line. Can be meal', 'eco' or 'gift'",
    mollieOnetimeSettingsManualCaptureLabel: 'Capture Payment Manually',
    mollieOnetimeSettingsManualCaptureDescription: 'By default, payments are captured automatically. This flag tells Mollie to authorize the payment only, allowing you to capture it manually later. Note that not all payment methods support manual capture.',

    // MetaData
    mollieOnetimeMetaDataMollieOrderIdLabel: 'Mollie Order Id (Obsolete. Switch to Payment Id in v17)',
    mollieOnetimeMetaDataMolliePaymentIdLabel: 'Mollie Payment Id',
    mollieOnetimeMetaDataMolliePaymentMethodLabel: 'Mollie Payment Method',
  },
};
