using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mollie.Api.Client;
using Mollie.Api.Models.Payment;
using Mollie.Api.Models.Payment.Request;
using Mollie.Api.Models.Payment.Response;
using NUnit.Framework;
using MollieOrderLineType = Mollie.Api.Models.Order.Request.OrderLineDetailsType;
using PaymentStatus = Umbraco.Commerce.Core.Models.PaymentStatus;

namespace Umbraco.Commerce.PaymentProviders.Mollie.Tests
{
    /// <summary>
    /// Checks what Mollie.Api 5 sends and how it reads responses, with a stub HTTP handler
    /// instead of the Mollie API.
    /// </summary>
    public class MollieClientTests
    {
        private const string PaymentJson = """
            {
              "resource": "payment",
              "id": "tr_WDqYK6vllg",
              "mode": "test",
              "createdAt": "2026-11-02T13:13:37+00:00",
              "status": "paid",
              "amount": { "currency": "EUR", "value": "100.00" },
              "amountRefunded": { "currency": "EUR", "value": "25.00" },
              "amountCaptured": { "currency": "EUR", "value": "100.00" },
              "description": "ORDER-01",
              "method": "ideal",
              "metadata": "ORDER-01",
              "profileId": "pfl_QkEhN94Ba",
              "sequenceType": "oneoff",
              "redirectUrl": "https://example.com/callback?redirect=true",
              "_links": {
                "self": { "href": "https://api.mollie.com/v2/payments/tr_WDqYK6vllg", "type": "application/hal+json" },
                "checkout": { "href": "https://www.mollie.com/checkout/select-method/WDqYK6vllg", "type": "text/html" },
                "dashboard": { "href": "https://www.mollie.com/dashboard/payments/tr_WDqYK6vllg", "type": "text/html" },
                "documentation": { "href": "https://docs.mollie.com/reference/create-payment", "type": "text/html" }
              }
            }
            """;

        private const string ErrorJson = """
            {
              "status": 422,
              "title": "Unprocessable Entity",
              "detail": "The amount contains an invalid number of decimals",
              "field": "amount.value",
              "_links": { "documentation": { "href": "https://docs.mollie.com/overview/handling-errors", "type": "text/html" } }
            }
            """;

        [Test]
        public async Task CreatePayment_Sends_Rounded_Amounts_And_Two_Decimal_Vat_Rates()
        {
            var handler = new StubHandler(HttpStatusCode.Created, PaymentJson);
            using var client = new PaymentClient("test_dummykey000000000000000000000", new HttpClient(handler));

            var request = new PaymentRequest
            {
                Amount = MollieAmounts.Create("EUR", 12.105m),
                Description = "ORDER-01",
                RedirectUrl = "https://example.com/callback?redirect=true",
                Lines =
                [
                    new PaymentLine
                    {
                        Sku = "SKU1",
                        Description = "Product",
                        Type = MollieOrderLineType.Physical,
                        Quantity = 1,
                        UnitPrice = MollieAmounts.Create("EUR", 12.105m),
                        VatRate = MollieAmounts.ToVatRate((12.10m / 10.00m) - 1),
                        VatAmount = MollieAmounts.Create("EUR", 2.1m),
                        TotalAmount = MollieAmounts.Create("EUR", 12.105m),
                    },
                    new PaymentLine
                    {
                        Sku = "GIFT_CARD",
                        Description = "Gift Card - ABC",
                        Type = MollieOrderLineType.GiftCard,
                        Quantity = 1,
                        UnitPrice = MollieAmounts.Create("EUR", -5m),
                        VatRate = MollieAmounts.ToVatRate(0m),
                        VatAmount = MollieAmounts.Create("EUR", 0m),
                        TotalAmount = MollieAmounts.Create("EUR", -5m),
                    },
                ],
            };

            PaymentResponse response = (await client.CreatePaymentAsync(request)).EnsureSuccess();

            Assert.That(response.Links.Checkout!.Href, Is.EqualTo("https://www.mollie.com/checkout/select-method/WDqYK6vllg"));

            JsonNode body = JsonNode.Parse(handler.LastRequestBody!)!;
            Assert.That(body["amount"]!["value"]!.GetValue<string>(), Is.EqualTo("12.11"));
            Assert.That(body["amount"]!["currency"]!.GetValue<string>(), Is.EqualTo("EUR"));

            JsonNode line = body["lines"]![0]!;
            Assert.That(line["vatRate"]!.GetValue<string>(), Is.EqualTo("21.00"));
            Assert.That(line["unitPrice"]!["value"]!.GetValue<string>(), Is.EqualTo("12.11"));
            Assert.That(line["vatAmount"]!["value"]!.GetValue<string>(), Is.EqualTo("2.10"));

            JsonNode giftCard = body["lines"]![1]!;
            Assert.That(giftCard["vatRate"]!.GetValue<string>(), Is.EqualTo("0.00"));
            Assert.That(giftCard["totalAmount"]!["value"]!.GetValue<string>(), Is.EqualTo("-5.00"));
            Assert.That(giftCard["type"]!.GetValue<string>(), Is.EqualTo("gift_card"));
        }

        [Test]
        public async Task GetPayment_Reads_Amounts_As_Decimals_And_Maps_The_Status()
        {
            var handler = new StubHandler(HttpStatusCode.OK, PaymentJson);
            using var client = new PaymentClient("test_dummykey000000000000000000000", new HttpClient(handler));

            PaymentResponse payment = (await client.GetPaymentAsync("tr_WDqYK6vllg")).EnsureSuccess();

            Assert.That(payment.Amount.Value, Is.EqualTo(100.00m));
            Assert.That(payment.Method, Is.EqualTo(PaymentMethod.Ideal));
            Assert.That(MollieOneTimePaymentProviderV2.GetPaymentStatus(payment), Is.EqualTo(PaymentStatus.PartiallyRefunded));
        }

        [Test]
        public void An_Api_Error_Still_Throws_With_EnsureSuccess()
        {
            var handler = new StubHandler(HttpStatusCode.UnprocessableEntity, ErrorJson);
            using var client = new PaymentClient("test_dummykey000000000000000000000", new HttpClient(handler));

            MollieApiException? ex = Assert.ThrowsAsync<MollieApiException>(async () =>
                (await client.GetPaymentAsync("tr_WDqYK6vllg")).EnsureSuccess());

            Assert.That(ex!.Details.Detail, Does.Contain("invalid number of decimals"));
        }

        [Test]
        public async Task An_Unchecked_Api_Error_Does_Not_Throw()
        {
            // Mollie.Api 5 returns API errors as a result; every call in the provider ends in
            // EnsureSuccess() so failures still surface as exceptions like they did in v4.
            var handler = new StubHandler(HttpStatusCode.UnprocessableEntity, ErrorJson);
            using var client = new PaymentClient("test_dummykey000000000000000000000", new HttpClient(handler));

            var result = await client.CancelPaymentAsync("tr_WDqYK6vllg");

            Assert.That(result.Success, Is.False);
        }

        private sealed class StubHandler(HttpStatusCode statusCode, string responseBody) : HttpMessageHandler
        {
            public string? LastRequestBody { get; private set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                LastRequestBody = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(statusCode)
                {
                    Content = new StringContent(responseBody, Encoding.UTF8, "application/hal+json"),
                };
            }
        }
    }
}
