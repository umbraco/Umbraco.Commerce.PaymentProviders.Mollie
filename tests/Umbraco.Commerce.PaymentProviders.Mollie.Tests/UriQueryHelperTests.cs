using Microsoft.AspNetCore.WebUtilities;
using NUnit.Framework;

namespace Umbraco.Commerce.PaymentProviders.Mollie.Tests
{
    public class UriQueryHelperTests
    {
        [TestCase("https://umbraco.com", "https://umbraco.com?addedparam=true", "adding a query parameter to an absolute url with no query parameters case failed")]
        [TestCase("https://umbraco.com?param1=a&param2=b", "https://umbraco.com?param1=a&param2=b&addedparam=true", "adding a query parameter to an absolute url with query parameters case failed")]
        [TestCase("/relative-url", "/relative-url?addedparam=true", "adding a query parameter to a relative url with no query parameters case failed")]
        [TestCase("/relative-url?param1=a&param2=b", "/relative-url?param1=a&param2=b&addedparam=true", "adding a query parameter to a relative url case failed")]
        public void AddQueryParameter_Should_Run_Successfully(string input, string expect, string errorMessage)
        {
            string actualOutput = QueryHelpers.AddQueryString(input, "addedparam", "true");

            Assert.That(actualOutput, Is.EqualTo(expect), errorMessage);
        }
    }
}
