export default {
  ucPaymentProviders: {
    mollieOnetimeV2Label: 'Mollie (One Time)',
    mollieOnetimeV2Description: 'Mollie payment provider for one time payments',
    mollieOnetimeV2SettingsContinueUrlLabel: 'Continue URL',
    mollieOnetimeV2SettingsContinueUrlDescription: 'The URL to continue to after this provider has done processing. eg: /continue/',
    mollieOnetimeV2SettingsCancelUrlLabel: 'Cancel URL',
    mollieOnetimeV2SettingsCancelUrlDescription: 'The URL to return to if the payment attempt is canceled. eg: /cart/',
    mollieOnetimeV2SettingsErrorUrlLabel: 'Error URL',
    mollieOnetimeV2SettingsErrorUrlDescription: 'The URL to return to if the payment attempt errors. eg: /error/',

    mollieOnetimeV2SettingsBillingAddressLine1PropertyAliasLabel: 'Billing Address (Line 1) Property Alias',
    mollieOnetimeV2SettingsBillingAddressLine1PropertyAliasDescription: '[Required] The order property alias containing line 1 of the billing address',

    mollieOnetimeV2SettingsBillingAddressCityPropertyAliasLabel: 'Billing Address City Property Alias',
    mollieOnetimeV2SettingsBillingAddressCityPropertyAliasDescription: '[Required] The order property alias containing the city of the billing address',

    mollieOnetimeV2SettingsBillingAddressStatePropertyAliasLabel: 'Billing Address State Property Alias',
    mollieOnetimeV2SettingsBillingAddressStatePropertyAliasDescription: 'The order property alias containing the state of the billing address',

    mollieOnetimeV2SettingsBillingAddressZipCodePropertyAliasLabel: 'Billing Address ZipCode Property Alias',
    mollieOnetimeV2SettingsBillingAddressZipCodePropertyAliasDescription: '[Required] The order property alias containing the zip code of the billing address',

    mollieOnetimeV2SettingsTestApiKeyLabel: 'Test API Key',
    mollieOnetimeV2SettingsTestApiKeyDescription: 'Your test Mollie API key',

    mollieOnetimeV2SettingsLiveApiKeyLabel: 'Live API Key',
    mollieOnetimeV2SettingsLiveApiKeyDescription: 'Your live Mollie API key',

    mollieOnetimeV2SettingsTestModeLabel: 'Test Mode',
    mollieOnetimeV2SettingsTestModeDescription: 'Set whether to process payments in test mode',

    mollieOnetimeV2SettingsLocaleLabel: 'Locale',
    mollieOnetimeV2SettingsLocaleDescription: 'The locale to display the payment provider portal in',

    mollieOnetimeV2SettingsPaymentMethodsLabel: 'Payment Methods',
    mollieOnetimeV2SettingsPaymentMethodsDescription: "A comma separated list of payment methods to limit the payment method selection screen by. Can be 'applepay', 'bancontact', 'banktransfer', 'belfius', 'creditcard', 'directdebit', 'eps', 'giftcard', 'giropay', 'ideal', 'kbc', 'klarnapaylater', 'klarnasliceit', mybank', 'paypal', 'paysafecard', 'przelewy24', 'sofort' or 'voucher'",

    mollieOnetimeV2SettingsOrderLineProductTypePropertyAliasLabel: 'Order Line Product Type Property Alias',
    mollieOnetimeV2SettingsOrderLineProductTypePropertyAliasDescription: "The order line property alias containing a Mollie product type for the order line. Can be either 'physical' or 'digital'",

    mollieOnetimeV2SettingsOrderLineProductCategoryPropertyAliasLabel: 'Order Line Product Category Property Alias',
    mollieOnetimeV2SettingsOrderLineProductCategoryPropertyAliasDescription: "The order line property alias containing a Mollie product category for the order line. Can be meal', 'eco' or 'gift'",
    mollieOnetimeV2SettingsManualCaptureLabel: 'Capture Payment Manually',
    mollieOnetimeV2SettingsManualCaptureDescription: 'By default, payments are captured automatically. This flag tells Mollie to authorize the payment only, allowing you to capture it manually later. Note that not all payment methods support manual capture.',

    // MetaData
    mollieOnetimeV2MetaDataMollieOrderIdLabel: 'Mollie Order Id (Obsolete. Switch to Payment Id in v17)',
    mollieOnetimeV2MetaDataMolliePaymentIdLabel: 'Mollie Payment Id',
    mollieOnetimeV2MetaDataMolliePaymentMethodLabel: 'Mollie Payment Method',
  },
};
