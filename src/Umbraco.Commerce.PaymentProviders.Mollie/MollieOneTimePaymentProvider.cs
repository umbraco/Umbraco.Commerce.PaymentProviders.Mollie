using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;
using Mollie.Api.Client;
using Mollie.Api.Models.Order;
using Mollie.Api.Models.Order.Request;
using Mollie.Api.Models.Order.Response;
using Mollie.Api.Models.Payment.Response.PaymentSpecificParameters;
using Mollie.Api.Models.Shipment.Request;
using Umbraco.Commerce.Common.Logging;
using Umbraco.Commerce.Core.Api;
using Umbraco.Commerce.Core.Models;
using Umbraco.Commerce.Core.PaymentProviders;
using Umbraco.Commerce.Extensions;
using MollieAmount = Mollie.Api.Models.Amount;
using MollieLocale = Mollie.Api.Models.Payment.Locale;
using MollieOrderLineType = Mollie.Api.Models.Order.Request.OrderLineDetailsType;
using MollieOrderLineStatus = Mollie.Api.Models.Order.Response.OrderLineStatus;
using MollieOrderStatus = Mollie.Api.Models.Order.Response.OrderStatus;
using MolliePaymentStatus = Mollie.Api.Models.Payment.PaymentStatus;

namespace Umbraco.Commerce.PaymentProviders.Mollie
{
    [PaymentProvider("mollie-onetime")]
    public class MollieOneTimePaymentProvider : PaymentProviderBase<MollieOneTimeSettings>
    {
        private const string MolliePaymentFailed = "failed";
        private ILogger<MollieOneTimePaymentProvider> _logger;
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

            if (ctx.HttpContext.Request.Query.TryGetValue(MollieFailureReasonQueryParam, out StringValues mollieFailureReason))
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
            CurrencyReadOnly currency = await Context.Services.CurrencyService.GetCurrencyAsync(ctx.Order.CurrencyId);
            CountryReadOnly country = await Context.Services.CountryService.GetCountryAsync(ctx.Order.PaymentInfo!.CountryId!.Value!);
            PaymentMethodReadOnly paymentMethod = await Context.Services.PaymentMethodService.GetPaymentMethodAsync(ctx.Order.PaymentInfo!.PaymentMethodId!.Value!);
            ShippingMethodReadOnly shippingMethod = ctx.Order.ShippingInfo.ShippingMethodId.HasValue
                ? await Context.Services.ShippingMethodService.GetShippingMethodAsync(ctx.Order.ShippingInfo.ShippingMethodId.Value)
                : null;

            // Adjustments helper
            var processPriceAdjustments = new Action<IReadOnlyCollection<PriceAdjustment>, List<OrderLineRequest>, string>((adjustments, orderLines, namePrefix) =>
            {
                foreach (PriceAdjustment adjustment in adjustments)
                {
                    bool isDiscount = adjustment.Price.WithTax < 0;
                    decimal taxRate = (adjustment.Price.WithTax / adjustment.Price.WithoutTax) - 1;

                    orderLines.Add(new OrderLineRequest
                    {
                        Sku = isDiscount ? "DISCOUNT" : "SURCHARGE",
                        Name = (namePrefix + " " + (isDiscount ? "Discount" : "Fee") + " - " + adjustment.Name).Trim(),
                        Type = isDiscount ? MollieOrderLineType.Discount : MollieOrderLineType.Surcharge,
                        Quantity = 1,
                        UnitPrice = new MollieAmount(currency.Code, adjustment.Price.WithTax),
                        VatRate = (taxRate * 100).ToString("0.00", CultureInfo.InvariantCulture),
                        VatAmount = new MollieAmount(currency.Code, adjustment.Price.Tax),
                        TotalAmount = new MollieAmount(currency.Code, adjustment.Price.WithTax)
                    });
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
                        UnitPrice = new MollieAmount(currency.Code, orderLine.UnitPrice.WithoutAdjustments.WithTax),
                        VatRate = (orderLine.TaxRate.Value * 100).ToString("0.00", CultureInfo.InvariantCulture),
                        VatAmount = new MollieAmount(currency.Code, orderLine.TotalPrice.Value.Tax),
                        TotalAmount = new MollieAmount(currency.Code, orderLine.TotalPrice.Value.WithTax)
                    };

                    if (orderLine.TotalPrice.TotalAdjustment.WithTax < 0)
                    {
                        mollieOrderLine.DiscountAmount = new MollieAmount(currency.Code, orderLine.TotalPrice.TotalAdjustment.WithTax * -1);
                    }
                    else if (orderLine.TotalPrice.TotalAdjustment.WithTax > 0)
                    {
                        // Not sure we can handle an order line fee?
                    }

                    if (!string.IsNullOrWhiteSpace(ctx.Settings.OrderLineProductTypePropertyAlias))
                    {
                        mollieOrderLine.Type = orderLine.Properties[ctx.Settings.OrderLineProductTypePropertyAlias];
                    }

                    if (!string.IsNullOrWhiteSpace(ctx.Settings.OrderLineProductCategoryPropertyAlias))
                    {
                        mollieOrderLine.Category = orderLine.Properties[ctx.Settings.OrderLineProductCategoryPropertyAlias];
                    }

                    mollieOrderLines.Add(mollieOrderLine);
                }

                // Process subtotal price adjustments
                if (ctx.Order.SubtotalPrice.Adjustments.Count > 0)
                {
                    processPriceAdjustments.Invoke(ctx.Order.SubtotalPrice.Adjustments, mollieOrderLines, "Subtotal");
                }

                // Process payment fee
                if (ctx.Order.PaymentInfo.TotalPrice.WithoutAdjustments.WithTax > 0)
                {
                    var paymentOrderLine = new OrderLineRequest
                    {
                        Sku = !string.IsNullOrWhiteSpace(paymentMethod.Sku) ? paymentMethod.Sku : "PF001",
                        Name = paymentMethod.Name + " Fee",
                        Type = MollieOrderLineType.Surcharge,
                        Quantity = 1,
                        UnitPrice = new MollieAmount(currency.Code, ctx.Order.PaymentInfo.TotalPrice.WithoutAdjustments.WithTax),
                        VatRate = (ctx.Order.PaymentInfo.TaxRate.Value * 100).ToString("0.00", CultureInfo.InvariantCulture),
                        VatAmount = new MollieAmount(currency.Code, ctx.Order.PaymentInfo.TotalPrice.Value.Tax),
                        TotalAmount = new MollieAmount(currency.Code, ctx.Order.PaymentInfo.TotalPrice.Value.WithTax)
                    };

                    if (ctx.Order.PaymentInfo.TotalPrice.Adjustment.WithTax < 0)
                    {
                        paymentOrderLine.DiscountAmount = new MollieAmount(currency.Code, ctx.Order.PaymentInfo.TotalPrice.Adjustment.WithTax * -1);
                    }
                    else if (ctx.Order.PaymentInfo.TotalPrice.Adjustment.WithTax > 0)
                    {
                        // Not sure we can handle an order line fee?
                    }

                    mollieOrderLines.Add(paymentOrderLine);
                }

                // Process shipping fee
                if (shippingMethod != null && ctx.Order.ShippingInfo.TotalPrice.WithoutAdjustments.WithTax > 0)
                {
                    var shippingOrderLine = new OrderLineRequest
                    {
                        Sku = !string.IsNullOrWhiteSpace(shippingMethod.Sku) ? shippingMethod.Sku : "SF001",
                        Name = shippingMethod.Name + " Fee",
                        Type = MollieOrderLineType.ShippingFee,
                        Quantity = 1,
                        UnitPrice = new MollieAmount(currency.Code, ctx.Order.ShippingInfo.TotalPrice.WithoutAdjustments.WithTax),
                        VatRate = (ctx.Order.ShippingInfo.TaxRate.Value * 100).ToString("0.00", CultureInfo.InvariantCulture),
                        VatAmount = new MollieAmount(currency.Code, ctx.Order.ShippingInfo.TotalPrice.Value.Tax),
                        TotalAmount = new MollieAmount(currency.Code, ctx.Order.ShippingInfo.TotalPrice.Value.WithTax)
                    };

                    if (ctx.Order.ShippingInfo.TotalPrice.Adjustment.WithTax < 0)
                    {
                        shippingOrderLine.DiscountAmount = new MollieAmount(currency.Code, ctx.Order.ShippingInfo.TotalPrice.Adjustment.WithTax * -1);
                    }
                    else if (ctx.Order.ShippingInfo.TotalPrice.Adjustment.WithTax > 0)
                    {
                        // Not sure we can handle an order line fee?
                    }

                    mollieOrderLines.Add(shippingOrderLine);
                }

                // Process total price adjustments
                if (ctx.Order.TotalPrice.Adjustments.Count > 0)
                {
                    processPriceAdjustments.Invoke(ctx.Order.TotalPrice.Adjustments, mollieOrderLines, "Total");
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
                            UnitPrice = new MollieAmount(currency.Code, giftCard.Amount.Value),
                            VatRate = "0.00",
                            VatAmount = new MollieAmount(currency.Code, 0m),
                            TotalAmount = new MollieAmount(currency.Code, giftCard.Amount.Value)
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
                            UnitPrice = new MollieAmount(currency.Code, adjustment.Amount.Value),
                            VatRate = "0.00",
                            VatAmount = new MollieAmount(currency.Code, 0m),
                            TotalAmount = new MollieAmount(currency.Code, adjustment.Amount.Value)
                        });
                    }
                }

                var mollieOrderRequest = new OrderRequest
                {
                    Amount = new MollieAmount(currency.Code.ToUpperInvariant(), ctx.Order.TransactionAmount.Value),
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
                    MetaData = new Dictionary<string, string>()
                {
                    { "mollieOrderId", mollieOrderResult.Id }
                }
                };
            }
        }

        public override async Task<CallbackResult> ProcessCallbackAsync(PaymentProviderContext<MollieOneTimeSettings> context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            if (context.HttpContext.Request.Query.ContainsKey("redirect"))
            {
                return await ProcessRedirectCallbackAsync(context, CancellationToken.None);
            }
            else
            {
                return await ProcessWebhookCallbackAsync(context, CancellationToken.None);
            }
        }

        private static async Task<CallbackResult> ProcessRedirectCallbackAsync(PaymentProviderContext<MollieOneTimeSettings> ctx, CancellationToken cancellationToken = default)
        {
            var result = new CallbackResult();

            PropertyValue mollieOrderId = ctx.Order.Properties["mollieOrderId"];
            using (var mollieOrderClient = new OrderClient(ctx.Settings.TestMode ? ctx.Settings.TestApiKey : ctx.Settings.LiveApiKey))
            {
                OrderResponse mollieOrder = await mollieOrderClient.GetOrderAsync(mollieOrderId, true);
                if (mollieOrder != null)
                {
                    var payments = (mollieOrder.Embedded?.Payments ?? []).ToList();
                    var lastPayment = payments.Last() as CreditCardPaymentResponse;
                    if (lastPayment?.Status == MolliePaymentStatus.Failed)
                    {
                        // Mollie redirects user here when the payment failed
                        result.ActionResult = new RedirectResult($"{ctx.Urls.ErrorUrl}?{MollieFailureReasonQueryParam}={lastPayment.Details?.FailureReason}", false);

                    }
                    else if (payments.All(x => x.Status == MolliePaymentStatus.Canceled))
                    {
                        result.ActionResult = new RedirectResult(ctx.Urls.CancelUrl, false);
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
                                PaymentStatus = PaymentStatus.PendingExternalSystem,
                            };
                        }

                        result.ActionResult = new RedirectResult(ctx.Urls.ContinueUrl, false);
                    }
                }
            }

            return result;
        }

        private static async Task<CallbackResult> ProcessWebhookCallbackAsync(PaymentProviderContext<MollieOneTimeSettings> ctx, CancellationToken cancellationToken = default)
        {
            // Validate the ID from the webhook matches the orders mollieOrderId property
            IFormCollection formData = await ctx.HttpContext.Request.ReadFormAsync(cancellationToken);
            string id = formData["id"];

            PropertyValue mollieOrderId = ctx.Order.Properties["mollieOrderId"];
            if (id != mollieOrderId)
            {
                return CallbackResult.Ok();
            }

            using var mollieOrderClient = new OrderClient(ctx.Settings.TestMode ? ctx.Settings.TestApiKey : ctx.Settings.LiveApiKey);
            OrderResponse mollieOrder = await mollieOrderClient.GetOrderAsync(mollieOrderId, true, true);
            PaymentStatus paymentStatus = await GetPaymentStatusAsync(ctx, mollieOrder, cancellationToken);

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
                    PaymentStatus = await GetPaymentStatusAsync(ctx, mollieOrder, cancellationToken),
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
                    PaymentStatus = await GetPaymentStatusAsync(ctx, mollieOrder, cancellationToken),
                }
            };
        }

        public override async Task<ApiResult> RefundPaymentAsync(PaymentProviderContext<MollieOneTimeSettings> ctx, CancellationToken cancellationToken = default)
        {
            PropertyValue mollieOrderId = ctx.Order.Properties["mollieOrderId"];
            using var mollieRefundClient = new RefundClient(ctx.Settings.TestMode ? ctx.Settings.TestApiKey : ctx.Settings.LiveApiKey);
            using var mollieOrderClient = new OrderClient(ctx.Settings.TestMode ? ctx.Settings.TestApiKey : ctx.Settings.LiveApiKey);

            await mollieRefundClient.CreateOrderRefundAsync(mollieOrderId, new OrderRefundRequest { Lines = new List<OrderLineDetails>() });

            OrderResponse mollieOrder = await mollieOrderClient.GetOrderAsync(mollieOrderId, true, true);

            return new ApiResult
            {
                TransactionInfo = new TransactionInfoUpdate()
                {
                    TransactionId = ctx.Order.TransactionInfo.TransactionId,
                    PaymentStatus = await GetPaymentStatusAsync(ctx, mollieOrder, cancellationToken),
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
                    PaymentStatus = await GetPaymentStatusAsync(ctx, mollieOrder, cancellationToken),
                }
            };
        }

        private static async Task<PaymentStatus> GetPaymentStatusAsync(PaymentProviderContext<MollieOneTimeSettings> ctx, OrderResponse order, CancellationToken cancellationToken = default)
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
            using (var mollieRefundClient = new RefundClient(ctx.Settings.TestMode ? ctx.Settings.TestApiKey : ctx.Settings.LiveApiKey))
            {
                global::Mollie.Api.Models.List.Response.ListResponse<global::Mollie.Api.Models.Refund.Response.RefundResponse> refunds = await mollieRefundClient.GetOrderRefundListAsync(order.Id);
                if (refunds.Items.Any(x => x.Status != MolliePaymentFailed))
                {
                    return PaymentStatus.Refunded;
                }
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
