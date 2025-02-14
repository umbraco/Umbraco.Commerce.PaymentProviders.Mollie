using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;
using Mollie.Api.Client;
using Mollie.Api.Models.Order;
using Mollie.Api.Models.Payment.Response;
using Mollie.Api.Models.Shipment;
using Umbraco.Commerce.Common.Logging;
using Umbraco.Commerce.Core.Api;
using Umbraco.Commerce.Core.Models;
using Umbraco.Commerce.Core.PaymentProviders;
using Umbraco.Commerce.Extensions;
using MollieAmmount = Mollie.Api.Models.Amount;
using MollieLocale = Mollie.Api.Models.Payment.Locale;
using MollieOrderLineStatus = Mollie.Api.Models.Order.OrderLineStatus;
using MollieOrderLineType = Mollie.Api.Models.Order.OrderLineDetailsType;
using MollieOrderStatus = Mollie.Api.Models.Order.OrderStatus;
using MolliePaymentStatus = Mollie.Api.Models.Payment.PaymentStatus;
using PaymentStatus = Umbraco.Commerce.Core.Models.PaymentStatus;

namespace Umbraco.Commerce.PaymentProviders.Mollie
{
    [PaymentProvider("mollie-onetime", "Mollie (One Time)", "Mollie payment provider for one time payments")]
    public class MollieOneTimePaymentProvider : PaymentProviderBase<MollieOneTimeSettings>
    {
        private readonly ILogger<MollieOneTimePaymentProvider> _logger;
        private const string MollieFailureReasonQueryParam = "mollieFailureReason";

        public MollieOneTimePaymentProvider(
            UmbracoCommerceContext ctx,
            ILogger<MollieOneTimePaymentProvider> logger)
            : base(ctx)
        {
            _logger = logger;
        }

        public override bool CanFetchPaymentStatus => true;
        public override bool CanCancelPayments => true;
        public override bool CanRefundPayments => true;
        public override bool CanCapturePayments => true;
        public override bool FinalizeAtContinueUrl => false;

        public override IEnumerable<TransactionMetaDataDefinition> TransactionMetaDataDefinitions => new[]{
            new TransactionMetaDataDefinition("mollieOrderId", "Mollie Order Id"),
            new TransactionMetaDataDefinition("molliePaymentMethod", "Mollie Payment Method"),
        };

        public override string GetCancelUrl(PaymentProviderContext<MollieOneTimeSettings> ctx)
        {
            ctx.Settings.MustNotBeNull("settings");
            ctx.Settings.CancelUrl.MustNotBeNull("settings.CancelUrl");
            return ctx.Settings.CancelUrl;
        }

        public override string GetErrorUrl(PaymentProviderContext<MollieOneTimeSettings> ctx)
        {
            ctx.Settings.MustNotBeNull("settings");
            ctx.Settings.ErrorUrl.MustNotBeNull("settings.ErrorUrl");

            Dictionary<string, StringValues> requestQueries = QueryHelpers.ParseQuery(ctx.Request.RequestUri.Query);
            if (requestQueries.TryGetValue(MollieFailureReasonQueryParam, out StringValues mollieFailureReason))
            {
                // pass the failure reason to the error url
                string errorUrlWithFailureReason = QueryHelpers.AddQueryString(ctx.Settings.ErrorUrl, MollieFailureReasonQueryParam, mollieFailureReason);
                return errorUrlWithFailureReason;
            }

            return ctx.Settings.ErrorUrl;
        }

        public override string GetContinueUrl(PaymentProviderContext<MollieOneTimeSettings> ctx)
        {
            ctx.Settings.MustNotBeNull("settings");
            ctx.Settings.ContinueUrl.MustNotBeNull("settings.ContinueUrl");

            return ctx.Settings.ContinueUrl;
        }

        public override async Task<PaymentFormResult> GenerateFormAsync(PaymentProviderContext<MollieOneTimeSettings> ctx, CancellationToken cancellationToken = default)
        {
            // Validate settings
            ctx.Settings.MustNotBeNull("settings");

            if (ctx.Settings.TestMode)
            {
                ctx.Settings.TestApiKey.MustNotBeNull("settings.TestApiKey");
            }
            else
            {
                ctx.Settings.LiveApiKey.MustNotBeNull("settings.LiveApiKey");
            }

            // Get entities
            CurrencyReadOnly currency = Context.Services.CurrencyService.GetCurrency(ctx.Order.CurrencyId);
            CountryReadOnly country = Context.Services.CountryService.GetCountry(ctx.Order.PaymentInfo.CountryId.Value);
            PaymentMethodReadOnly paymentMethod = Context.Services.PaymentMethodService.GetPaymentMethod(ctx.Order.PaymentInfo.PaymentMethodId.Value);
            ShippingMethodReadOnly shippingMethod = ctx.Order.ShippingInfo.ShippingMethodId.HasValue
                ? Context.Services.ShippingMethodService.GetShippingMethod(ctx.Order.ShippingInfo.ShippingMethodId.Value)
                : null;

            // Adjustments helper
            var processAdjustmentPrice = new Action<Price, List<OrderLineRequest>, string, int>((price, orderLines, name, quantity) =>
            {
                bool isDiscount = price.WithTax < 0;
                decimal taxRate = (price.WithTax / price.WithoutTax) - 1;

                orderLines.Add(new OrderLineRequest
                {
                    Sku = isDiscount ? "DISCOUNT" : "SURCHARGE",
                    Name = name,
                    Type = isDiscount ? MollieOrderLineType.Discount : MollieOrderLineType.Surcharge,
                    Quantity = quantity,
                    UnitPrice = new MollieAmmount(currency.Code, price.WithTax),
                    VatRate = (taxRate * 100).ToString("0.00", CultureInfo.InvariantCulture),
                    VatAmount = new MollieAmmount(currency.Code, price.Tax * quantity),
                    TotalAmount = new MollieAmmount(currency.Code, price.WithTax * quantity),
                });
            });

            var processPriceAdjustment = new Action<PriceAdjustment, List<OrderLineRequest>, string, int>((adjustment, orderLines, namePrefix, quantity) =>
            {
                bool isDiscount = adjustment.Price.WithTax < 0;
                processAdjustmentPrice.Invoke(adjustment.Price, orderLines, (namePrefix + " " + (isDiscount ? "Discount" : "Fee") + " - " + adjustment.Name).Trim(), quantity);
            });

            var processPriceAdjustments = new Action<IReadOnlyCollection<PriceAdjustment>, List<OrderLineRequest>, string, int>((adjustments, orderLines, namePrefix, quantity) =>
            {
                foreach (PriceAdjustment adjustment in adjustments)
                {
                    processPriceAdjustment.Invoke(adjustment, orderLines, namePrefix, quantity);
                }
            });

            // Create the order
            using (var mollieOrderClient = new OrderClient(ctx.Settings.TestMode ? ctx.Settings.TestApiKey : ctx.Settings.LiveApiKey))
            {
                var mollieOrderAddress = new OrderAddressDetails
                {
                    GivenName = ctx.Order.CustomerInfo.FirstName,
                    FamilyName = ctx.Order.CustomerInfo.LastName,
                    Email = ctx.Order.CustomerInfo.Email,
                    Country = country.Code,
                };

                if (!string.IsNullOrWhiteSpace(ctx.Settings.BillingAddressLine1PropertyAlias))
                {
                    mollieOrderAddress.StreetAndNumber = ctx.Order.Properties[ctx.Settings.BillingAddressLine1PropertyAlias];
                }

                if (!string.IsNullOrWhiteSpace(ctx.Settings.BillingAddressCityPropertyAlias))
                {
                    mollieOrderAddress.City = ctx.Order.Properties[ctx.Settings.BillingAddressCityPropertyAlias];
                }

                if (!string.IsNullOrWhiteSpace(ctx.Settings.BillingAddressStatePropertyAlias))
                {
                    mollieOrderAddress.Region = ctx.Order.Properties[ctx.Settings.BillingAddressStatePropertyAlias];
                }

                if (!string.IsNullOrWhiteSpace(ctx.Settings.BillingAddressZipCodePropertyAlias))
                {
                    mollieOrderAddress.PostalCode = ctx.Order.Properties[ctx.Settings.BillingAddressZipCodePropertyAlias];
                }

                var mollieOrderLines = new List<OrderLineRequest>();

                // Process order lines
                foreach (OrderLineReadOnly orderLine in ctx.Order.OrderLines)
                {
                    var mollieOrderLine = new OrderLineRequest
                    {
                        Sku = orderLine.Sku,
                        Name = orderLine.Name,
                        Quantity = (int)orderLine.Quantity,
                        UnitPrice = new MollieAmmount(currency.Code, orderLine.UnitPrice.WithoutAdjustments.WithTax),
                        VatRate = (orderLine.TaxRate.Value * 100).ToString("0.00", CultureInfo.InvariantCulture),
                        VatAmount = new MollieAmmount(currency.Code, orderLine.TotalPrice.WithoutAdjustments.Tax),
                        TotalAmount = new MollieAmmount(currency.Code, orderLine.TotalPrice.WithoutAdjustments.WithTax),
                    };

                    if (!string.IsNullOrWhiteSpace(ctx.Settings.OrderLineProductTypePropertyAlias))
                    {
                        mollieOrderLine.Type = orderLine.Properties[ctx.Settings.OrderLineProductTypePropertyAlias];
                    }

                    if (!string.IsNullOrWhiteSpace(ctx.Settings.OrderLineProductCategoryPropertyAlias))
                    {
                        mollieOrderLine.Category = orderLine.Properties[ctx.Settings.OrderLineProductCategoryPropertyAlias];
                    }

                    mollieOrderLines.Add(mollieOrderLine);

                    // Because an order line can have sub order lines and various discounts and fees
                    // can apply, rather than adding each discount or fee to each order line, we
                    // add a single adjustment to the whole primary order line.
                    if (orderLine.TotalPrice.TotalAdjustment.WithTax != 0)
                    {
                        var isDiscount = orderLine.TotalPrice.TotalAdjustment.WithTax < 0;
                        var name = (orderLine.Name + " " + (isDiscount ? "Discount" : "Fee")).Trim();

                        processAdjustmentPrice.Invoke(orderLine.TotalPrice.TotalAdjustment, mollieOrderLines, name, 1);
                    }
                }

                // Process subtotal price adjustments
                if (ctx.Order.SubtotalPrice.Adjustments.Count > 0)
                {
                    processPriceAdjustments.Invoke(ctx.Order.SubtotalPrice.Adjustments, mollieOrderLines, "Subtotal", 1);
                }

                // Process payment fee
                if (ctx.Order.PaymentInfo.TotalPrice.WithoutAdjustments.WithTax > 0)
                {
                    var name = $"{paymentMethod.Name} Charge";

                    var paymentOrderLine = new OrderLineRequest
                    {
                        Sku = !string.IsNullOrWhiteSpace(paymentMethod.Sku) ? paymentMethod.Sku : "PF001",
                        Name = name,
                        Type = MollieOrderLineType.Surcharge,
                        Quantity = 1,
                        UnitPrice = new MollieAmmount(currency.Code, ctx.Order.PaymentInfo.TotalPrice.WithoutAdjustments.WithTax),
                        VatRate = (ctx.Order.PaymentInfo.TaxRate.Value * 100).ToString("0.00", CultureInfo.InvariantCulture),
                        VatAmount = new MollieAmmount(currency.Code, ctx.Order.PaymentInfo.TotalPrice.WithoutAdjustments.Tax),
                        TotalAmount = new MollieAmmount(currency.Code, ctx.Order.PaymentInfo.TotalPrice.WithoutAdjustments.WithTax),
                    };

                    mollieOrderLines.Add(paymentOrderLine);

                    if (ctx.Order.PaymentInfo.TotalPrice.Adjustments.Count > 0)
                    {
                        processPriceAdjustments.Invoke(ctx.Order.PaymentInfo.TotalPrice.Adjustments, mollieOrderLines, name, 1);
                    }
                }

                // Process shipping fee
                if (shippingMethod != null && ctx.Order.ShippingInfo.TotalPrice.WithoutAdjustments.WithTax > 0)
                {
                    var name = $"{shippingMethod.Name} Charge";

                    var shippingOrderLine = new OrderLineRequest
                    {
                        Sku = !string.IsNullOrWhiteSpace(shippingMethod.Sku) ? shippingMethod.Sku : "SF001",
                        Name = shippingMethod.Name + " Fee",
                        Type = MollieOrderLineType.ShippingFee,
                        Quantity = 1,
                        UnitPrice = new MollieAmmount(currency.Code, ctx.Order.ShippingInfo.TotalPrice.WithoutAdjustments.WithTax),
                        VatRate = (ctx.Order.ShippingInfo.TaxRate.Value * 100).ToString("0.00", CultureInfo.InvariantCulture),
                        VatAmount = new MollieAmmount(currency.Code, ctx.Order.ShippingInfo.TotalPrice.WithoutAdjustments.Tax),
                        TotalAmount = new MollieAmmount(currency.Code, ctx.Order.ShippingInfo.TotalPrice.WithoutAdjustments.WithTax),
                    };

                    mollieOrderLines.Add(shippingOrderLine);

                    if (ctx.Order.ShippingInfo.TotalPrice.Adjustments.Count > 0)
                    {
                        processPriceAdjustments.Invoke(ctx.Order.ShippingInfo.TotalPrice.Adjustments, mollieOrderLines, name, 1);
                    }
                }

                // Process total price adjustments
                if (ctx.Order.TotalPrice.Adjustments.Count > 0)
                {
                    processPriceAdjustments.Invoke(ctx.Order.TotalPrice.Adjustments, mollieOrderLines, "Total", 1);
                }

                // Process gift cards
                var giftCards = ctx.Order.TransactionAmount.Adjustments.OfType<GiftCardAdjustment>().ToList();
                if (giftCards.Count > 0)
                {
                    foreach (GiftCardAdjustment giftCard in giftCards)
                    {
                        mollieOrderLines.Add(new OrderLineRequest
                        {
                            Sku = "GIFT_CARD",
                            Name = "Gift Card - " + giftCard.GiftCardCode,
                            Type = MollieOrderLineType.GiftCard,
                            Quantity = 1,
                            UnitPrice = new MollieAmmount(currency.Code, giftCard.Amount.Value),
                            VatRate = "0.00",
                            VatAmount = new MollieAmmount(currency.Code, 0m),
                            TotalAmount = new MollieAmmount(currency.Code, giftCard.Amount.Value),
                        });
                    }
                }

                // Process other adjustment types
                var amountAdjustments = ctx.Order.TransactionAmount.Adjustments.Where(x => !(x is GiftCardAdjustment)).ToList();
                if (amountAdjustments.Count > 0)
                {
                    foreach (AmountAdjustment adjustment in amountAdjustments)
                    {
                        bool isDiscount = adjustment.Amount.Value < 0;

                        mollieOrderLines.Add(new OrderLineRequest
                        {
                            Sku = isDiscount ? "DISCOUNT" : "SURCHARGE",
                            Name = "Transaction " + (isDiscount ? "Discount" : "Fee") + " - " + adjustment.Name,
                            Type = isDiscount ? MollieOrderLineType.Discount : MollieOrderLineType.Surcharge,
                            Quantity = 1,
                            UnitPrice = new MollieAmmount(currency.Code, adjustment.Amount.Value),
                            VatRate = "0.00",
                            VatAmount = new MollieAmmount(currency.Code, 0m),
                            TotalAmount = new MollieAmmount(currency.Code, adjustment.Amount.Value),
                        });
                    }
                }

                var mollieOrderRequest = new OrderRequest
                {
                    Amount = new MollieAmmount(currency.Code.ToUpperInvariant(), ctx.Order.TransactionAmount.Value),
                    OrderNumber = ctx.Order.OrderNumber,
                    Lines = mollieOrderLines,
                    Metadata = ctx.Order.GenerateOrderReference(),
                    BillingAddress = mollieOrderAddress,
                    RedirectUrl = ctx.Urls.CallbackUrl + "?redirect=true", // Explicitly redirect to the callback URL as this will need to do more processing to decide where to redirect to
                    WebhookUrl = ctx.Urls.CallbackUrl,
                    Locale = !string.IsNullOrWhiteSpace(ctx.Settings.Locale) ? ctx.Settings.Locale : MollieLocale.en_US,
                };

                if (!string.IsNullOrWhiteSpace(ctx.Settings.PaymentMethods))
                {
                    var paymentMethods = ctx.Settings.PaymentMethods.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => x.Trim())
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .ToList();

                    if (paymentMethods.Count == 1)
                    {
                        mollieOrderRequest.Method = paymentMethods[0];
                    }
                    else if (paymentMethods.Count > 1)
                    {
                        mollieOrderRequest.Methods = paymentMethods;
                    }
                }

                OrderResponse mollieOrderResult = await mollieOrderClient.CreateOrderAsync(mollieOrderRequest);

                return new PaymentFormResult
                {
                    Form = new PaymentForm(mollieOrderResult.Links.Checkout.Href, PaymentFormMethod.Get),
                    MetaData = new Dictionary<string, string>
                    {
                        { "mollieOrderId", mollieOrderResult.Id },
                    },
                };
            }
        }

        public override async Task<CallbackResult> ProcessCallbackAsync(PaymentProviderContext<MollieOneTimeSettings> ctx, CancellationToken cancellationToken = default)
        {
            if (ctx.Request.RequestUri.Query.Contains("redirect", StringComparison.OrdinalIgnoreCase))
            {
                return await ProcessRedirectCallbackAsync(ctx, cancellationToken);
            }
            else
            {
                return await ProcessWebhookCallbackAsync(ctx, cancellationToken);
            }
        }

        private async Task<CallbackResult> ProcessRedirectCallbackAsync(PaymentProviderContext<MollieOneTimeSettings> ctx, CancellationToken cancellationToken = default)
        {
            var result = new CallbackResult
            {
                HttpResponse = new HttpResponseMessage(System.Net.HttpStatusCode.Found)
            };

            PropertyValue mollieOrderId = ctx.Order.Properties["mollieOrderId"];
            _logger.Info($"ProcessRedirectCallbackAsync; mollieOrderId: '{mollieOrderId}'; ctx.Order.OrderNumber: '{ctx.Order.OrderNumber}'.");
            using (var mollieOrderClient = new OrderClient(ctx.Settings.TestMode ? ctx.Settings.TestApiKey : ctx.Settings.LiveApiKey))
            {
                OrderResponse mollieOrder = await mollieOrderClient.GetOrderAsync(mollieOrderId, true);
                if (mollieOrder != null)
                {
                    var lastPayment = mollieOrder.Embedded.Payments.Last() as CreditCardPaymentResponse;
                    if (lastPayment?.Status == MolliePaymentStatus.Failed)
                    {
                        // Mollie redirects user here when the payment failed
                        var errorUri = new Uri($"{ctx.Urls.ErrorUrl}?{MollieFailureReasonQueryParam}={lastPayment.Details.FailureReason}");
                        result.HttpResponse.Headers.Location = errorUri;
                    }
                    else if (mollieOrder.Embedded.Payments.All(x => x.Status == MolliePaymentStatus.Canceled))
                    {
                        result.HttpResponse.Headers.Location = new Uri(ctx.Urls.CancelUrl);
                    }
                    else
                    {
                        // If the order is pending, Mollie won't sent a webhook notification so
                        // we check for this on the return URL and if the order is pending, finalize it
                        // and set it's status to pending before progressing to the confirmation page
                        if (mollieOrder.Status.Equals("pending", StringComparison.OrdinalIgnoreCase))
                        {
                            result.TransactionInfo = new TransactionInfo
                            {
                                AmountAuthorized = decimal.Parse(mollieOrder.Amount.Value, CultureInfo.InvariantCulture),
                                TransactionFee = 0m,
                                TransactionId = mollieOrderId,
                                PaymentStatus = PaymentStatus.PendingExternalSystem
                            };
                        }

                        result.HttpResponse.Headers.Location = new Uri(ctx.Urls.ContinueUrl);
                    }

                    result.MetaData = new Dictionary<string, string>()
                    {
                        { "mollieOrderId", mollieOrder.Id },
                        { "molliePaymentMethod", mollieOrder.Method },
                    };
                }
            }

            return result;
        }

        private async Task<CallbackResult> ProcessWebhookCallbackAsync(PaymentProviderContext<MollieOneTimeSettings> ctx, CancellationToken cancellationToken = default)
        {
            // Validate the ID from the webhook matches the orders mollieOrderId property
            System.Collections.Specialized.NameValueCollection formData = await ctx.Request.Content.ReadAsFormDataAsync(cancellationToken: cancellationToken);
            string id = formData["id"];

            PropertyValue mollieOrderId = ctx.Order.Properties["mollieOrderId"];
            _logger.Info($"ProcessWebhookCallbackAsync; orderIdFromCallback: '{id}'; mollieOrderId: '{mollieOrderId}'; ctx.Order.OrderNumber: '{ctx.Order.OrderNumber}'.");
            if (id != mollieOrderId)
            {
                return CallbackResult.Ok();
            }

            using var mollieOrderClient = new OrderClient(ctx.Settings.TestMode ? ctx.Settings.TestApiKey : ctx.Settings.LiveApiKey);
            OrderResponse mollieOrder = await mollieOrderClient.GetOrderAsync(mollieOrderId, true, true);
            PaymentStatus paymentStatus = await GetPaymentStatusAsync(mollieOrderClient, mollieOrder, cancellationToken);

            // Mollie sends cancelled notifications for unfinalized orders so we need to ensure that
            // we only cancel orders that are authorized
            if (paymentStatus == PaymentStatus.Cancelled && ctx.Order.TransactionInfo.PaymentStatus != PaymentStatus.Authorized)
            {
                return CallbackResult.Ok();
            }

            return CallbackResult.Ok(
                new TransactionInfo
                {
                    AmountAuthorized = decimal.Parse(mollieOrder.Amount.Value, CultureInfo.InvariantCulture),
                    TransactionFee = 0m,
                    TransactionId = mollieOrderId,
                    PaymentStatus = paymentStatus
                },
                new Dictionary<string, string>
                {
                    { "mollieOrderId", mollieOrder.Id },
                    { "molliePaymentMethod", mollieOrder.Method }
                });
        }

        public override async Task<ApiResult> FetchPaymentStatusAsync(PaymentProviderContext<MollieOneTimeSettings> ctx, CancellationToken cancellationToken = default)
        {
            PropertyValue mollieOrderId = ctx.Order.Properties["mollieOrderId"];
            using var mollieOrderClient = new OrderClient(ctx.Settings.TestMode ? ctx.Settings.TestApiKey : ctx.Settings.LiveApiKey);
            OrderResponse mollieOrder = await mollieOrderClient.GetOrderAsync(mollieOrderId, true, true);

            return new ApiResult
            {
                TransactionInfo = new TransactionInfoUpdate()
                {
                    TransactionId = ctx.Order.TransactionInfo.TransactionId,
                    PaymentStatus = await GetPaymentStatusAsync(mollieOrderClient, mollieOrder, cancellationToken)
                }
            };
        }

        public override async Task<ApiResult> CancelPaymentAsync(PaymentProviderContext<MollieOneTimeSettings> ctx, CancellationToken cancellationToken = default)
        {
            PropertyValue mollieOrderId = ctx.Order.Properties["mollieOrderId"];
            using var mollieOrderClient = new OrderClient(ctx.Settings.TestMode ? ctx.Settings.TestApiKey : ctx.Settings.LiveApiKey);

            await mollieOrderClient.CancelOrderAsync(mollieOrderId);

            OrderResponse mollieOrder = await mollieOrderClient.GetOrderAsync(mollieOrderId, true, true);

            return new ApiResult
            {
                TransactionInfo = new TransactionInfoUpdate()
                {
                    TransactionId = ctx.Order.TransactionInfo.TransactionId,
                    PaymentStatus = await GetPaymentStatusAsync(mollieOrderClient, mollieOrder, cancellationToken)
                }
            };
        }

        public override async Task<ApiResult> RefundPaymentAsync(PaymentProviderContext<MollieOneTimeSettings> ctx, CancellationToken cancellationToken = default)
        {
            PropertyValue mollieOrderId = ctx.Order.Properties["mollieOrderId"];
            using var mollieOrderClient = new OrderClient(ctx.Settings.TestMode ? ctx.Settings.TestApiKey : ctx.Settings.LiveApiKey);

            await mollieOrderClient.CreateOrderRefundAsync(mollieOrderId, new OrderRefundRequest { Lines = new List<OrderLineDetails>() });

            OrderResponse mollieOrder = await mollieOrderClient.GetOrderAsync(mollieOrderId, true, true);

            return new ApiResult
            {
                TransactionInfo = new TransactionInfoUpdate()
                {
                    TransactionId = ctx.Order.TransactionInfo.TransactionId,
                    PaymentStatus = await GetPaymentStatusAsync(mollieOrderClient, mollieOrder, cancellationToken)
                }
            };
        }

        public override async Task<ApiResult> CapturePaymentAsync(PaymentProviderContext<MollieOneTimeSettings> ctx, CancellationToken cancellationToken = default)
        {
            PropertyValue mollieOrderId = ctx.Order.Properties["mollieOrderId"];
            using var mollieShipmentClient = new ShipmentClient(ctx.Settings.TestMode ? ctx.Settings.TestApiKey : ctx.Settings.LiveApiKey);
            using var mollieOrderClient = new OrderClient(ctx.Settings.TestMode ? ctx.Settings.TestApiKey : ctx.Settings.LiveApiKey);

            await mollieShipmentClient.CreateShipmentAsync(mollieOrderId, new ShipmentRequest());

            OrderResponse mollieOrder = await mollieOrderClient.GetOrderAsync(mollieOrderId, true, true);

            return new ApiResult
            {
                TransactionInfo = new TransactionInfoUpdate()
                {
                    TransactionId = ctx.Order.TransactionInfo.TransactionId,
                    PaymentStatus = await GetPaymentStatusAsync(mollieOrderClient, mollieOrder, cancellationToken)
                }
            };
        }

        private async Task<PaymentStatus> GetPaymentStatusAsync(OrderClient orderClient, OrderResponse order, CancellationToken cancellationToken = default)
        {
            // The order is refunded if the total refunded amount is
            // greater than or equal to the original amount of the order
            if (order.AmountRefunded != null)
            {
                decimal amount = decimal.Parse(order.Amount.Value, CultureInfo.InvariantCulture);
                decimal amountRefunded = decimal.Parse(order.AmountRefunded.Value, CultureInfo.InvariantCulture);

                if (amountRefunded >= amount)
                {
                    return PaymentStatus.Refunded;
                }
            }

            // If there are any open refunds that are not in a failed status
            // we'll just assume to the order is refunded untill we know otherwise
            global::Mollie.Api.Models.List.ListResponse<global::Mollie.Api.Models.Refund.RefundResponse> refunds = await orderClient.GetOrderRefundListAsync(order.Id);
            if (refunds?.Items != null && refunds.Items.Any(x => x.Status != "failed"))
            {
                return PaymentStatus.Refunded;
            }

            // If the order is in a shipping status, at least one of the order lines
            // should be in an authorized or paid status. If there are any authorized
            // rows, then set the whole order as authorized, otherwise we'll see it's
            // captured.
            if (order.Status == MollieOrderStatus.Shipping)
            {
                if (order.Lines.Any(x => x.Status == MollieOrderLineStatus.Authorized))
                {
                    return PaymentStatus.Authorized;
                }
                else
                {
                    return PaymentStatus.Captured;
                }
            }

            // If the order is completed, there is at least one order line that is completed and
            // paid for. If all order lines were canceled, then the whole order would be cancelled
            if (order.Status == MollieOrderStatus.Paid || order.Status == MollieOrderStatus.Completed)
            {
                return PaymentStatus.Captured;
            }

            if (order.Status == MollieOrderStatus.Canceled || order.Status == MollieOrderStatus.Expired)
            {
                return PaymentStatus.Cancelled;
            }

            if (order.Status == MollieOrderStatus.Authorized)
            {
                return PaymentStatus.Authorized;
            }

            return PaymentStatus.PendingExternalSystem;
        }
    }
}
