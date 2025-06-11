using System;

namespace Umbraco.Commerce.PaymentProviders.Mollie
{
    /// <summary>
    /// This is the general exception class for the package, allowing consumers to catch exceptions thrown specifically by it.
    /// </summary>
    public class MolliePaymentProviderGeneralException : Exception
    {
        public MolliePaymentProviderGeneralException()
        {
        }

        public MolliePaymentProviderGeneralException(string message) : base(message)
        {
        }

        public MolliePaymentProviderGeneralException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
