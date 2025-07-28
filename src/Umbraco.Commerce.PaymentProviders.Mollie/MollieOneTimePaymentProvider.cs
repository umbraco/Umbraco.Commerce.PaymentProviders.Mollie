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
using Mollie.Api.Models.Refund.Response;
using Mollie.Api.Models.Shipment.Request;
using Umbraco.Commerce.Core.Api;
using Umbraco.Commerce.Core.Models;
using Umbraco.Commerce.Core.PaymentProviders;
using Umbraco.Commerce.Core.Services;
using Umbraco.Commerce.Extensions;
using MollieAmount = Mollie.Api.Models.Amount;
using MollieLocale = Mollie.Api.Models.Payment.Locale;
using MollieOrderLineStatus = Mollie.Api.Models.Order.Response.OrderLineStatus;
using MollieOrderLineType = Mollie.Api.Models.Order.Request.OrderLineDetailsType;
using MollieOrderStatus = Mollie.Api.Models.Order.Response.OrderStatus;
using MolliePaymentStatus = Mollie.Api.Models.Payment.PaymentStatus;
using MollieRefundRequest = Mollie.Api.Models.Refund.Request.RefundRequest;

namespace Umbraco.Commerce.PaymentProviders.Mollie
{
    [Obsolete("Will be removed in v17. Use MollieOneTimePaymentProvider instead")]
    [PaymentProvider("mollie-onetime")]
    public class MollieOneTimePaymentProvider : PaymentProviderBase<MollieOneTimeSettings>
    {
        private const string MolliePaymentFailed = "failed";
        private readonly IStoreService _storeService;
        private const string MollieFailureReasonQueryParam = "mollieFailureReason";

        public MollieOneTimePaymentProvider(
            UmbracoCommerceContext ctx,
            IStoreService storeService)
            : base(ctx)
        {
            _storeService = storeService;
        }

        public override bool CanFetchPaymentStatus => true;
        public override bool CanCancelPayments => true;
        public override bool CanRefundPayments => true;
        public override bool CanCapturePayments => true;
        public override bool FinalizeAtContinueUrl => false;
        public override bool CanPartiallyRefundPayments => true;

        public override IEnumerable<TransactionMetaDataDefinition> TransactionMetaDataDefinitions => [
            new TransactionMetaDataDefinition("mollieOrderId"),
            new TransactionMetaDataDefinition("molliePaymentMethod"),
            new TransactionMetaDataDefinition("molliePaymentId"),
        ];

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
                    UnitPrice = new MollieAmount(currency.Code, price.WithTax),
                    VatRate = (taxRate * 100).ToString("0.00", CultureInfo.InvariantCulture),
                    VatAmount = new MollieAmount(currency.Code, price.Tax * quantity),
                    TotalAmount = new MollieAmount(currency.Code, price.WithTax * quantity),
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
                        UnitPrice = new MollieAmount(currency.Code, orderLine.UnitPrice.WithoutAdjustments.WithTax),
                        VatRate = (orderLine.TaxRate.Value * 100).ToString("0.00", CultureInfo.InvariantCulture),
                        VatAmount = new MollieAmount(currency.Code, orderLine.TotalPrice.Value.Tax),
                        TotalAmount = new MollieAmount(currency.Code, orderLine.TotalPrice.Value.WithTax)
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
                        UnitPrice = new MollieAmount(currency.Code, ctx.Order.PaymentInfo.TotalPrice.WithoutAdjustments.WithTax),
                        VatRate = (ctx.Order.PaymentInfo.TaxRate.Value * 100).ToString("0.00", CultureInfo.InvariantCulture),
                        VatAmount = new MollieAmount(currency.Code, ctx.Order.PaymentInfo.TotalPrice.Value.Tax),
                        TotalAmount = new MollieAmount(currency.Code, ctx.Order.PaymentInfo.TotalPrice.Value.WithTax)
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
                        Name = name,
                        Type = MollieOrderLineType.ShippingFee,
                        Quantity = 1,
                        UnitPrice = new MollieAmount(currency.Code, ctx.Order.ShippingInfo.TotalPrice.WithoutAdjustments.WithTax),
                        VatRate = (ctx.Order.ShippingInfo.TaxRate.Value * 100).ToString("0.00", CultureInfo.InvariantCulture),
                        VatAmount = new MollieAmount(currency.Code, ctx.Order.ShippingInfo.TotalPrice.Value.Tax),
                        TotalAmount = new MollieAmount(currency.Code, ctx.Order.ShippingInfo.TotalPrice.Value.WithTax)
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
                    PaymentStatus = paymentStatus,
                },
                new Dictionary<string, string>
                {
                    { "mollieOrderId", mollieOrder.Id },
                    { "molliePaymentMethod", mollieOrder.Method },
                    { "molliePaymentId", (mollieOrder.Embedded?.Payments ?? []).FirstOrDefault(x => x.Status == MolliePaymentStatus.Paid)?.Id ?? string.Empty },
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

        [Obsolete("Will be removed in v16. Use the overload that takes an order refund request")]
        public override async Task<ApiResult?> RefundPaymentAsync(PaymentProviderContext<MollieOneTimeSettings> ctx, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(ctx);

            StoreReadOnly store = await _storeService.GetStoreAsync(ctx.Order.StoreId);
            Amount refundAmount = store.CanRefundTransactionFee ? ctx.Order.TransactionInfo.AmountAuthorized + ctx.Order.TransactionInfo.TransactionFee : ctx.Order.TransactionInfo.AmountAuthorized;
            return await this.RefundPaymentAsync(
                ctx,
                new PaymentProviderOrderRefundRequest
                {
                    RefundAmount = refundAmount,
                    Orderlines = [],
                },
                cancellationToken);
        }

        public override async Task<ApiResult?> RefundPaymentAsync(
            PaymentProviderContext<MollieOneTimeSettings> context,
            PaymentProviderOrderRefundRequest refundRequest,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(refundRequest);

            string? molliePaymentId = context.Order.Properties["molliePaymentId"]?.Value;
            string mollieOrderId = context.Order.Properties["mollieOrderId"]?.Value
                ?? throw new MolliePaymentProviderGeneralException($"Mollie Order Id could not be found on the order number '{context.Order.OrderNumber}'.");
            if (string.IsNullOrEmpty(molliePaymentId))
            {
                // look for the payment id using order
                using var mollieOrderClient = new OrderClient(context.Settings.TestMode ? context.Settings.TestApiKey : context.Settings.LiveApiKey);
                OrderResponse mollieOrder = await mollieOrderClient.GetOrderAsync(mollieOrderId, true, true);
                molliePaymentId = (mollieOrder.Embedded?.Payments ?? []).FirstOrDefault(x => x.Status == MolliePaymentStatus.Paid)?.Id;
            }

            if (string.IsNullOrEmpty(molliePaymentId))
            {
                throw new MolliePaymentProviderGeneralException($"Mollie Payment Id could not be found on the order number '{context.Order.OrderNumber}'.");
            }

            CurrencyReadOnly currency = await Context.Services.CurrencyService.GetCurrencyAsync(context.Order.CurrencyId);
            using var mollieRefundClient = new RefundClient(context.Settings.TestMode ? context.Settings.TestApiKey : context.Settings.LiveApiKey);
            RefundResponse refundResponse = await mollieRefundClient.CreatePaymentRefundAsync(molliePaymentId, new MollieRefundRequest
            {
                Amount = new MollieAmount(currency.Code, refundRequest.RefundAmount),
            });

            if (refundResponse.Status == RefundStatus.Failed)
            {
                throw new MolliePaymentProviderGeneralException($"Failed to refund the order number '{context.Order.OrderNumber}'. Mollie Payment Id: '{molliePaymentId}'");
            }

            return new ApiResult
            {
                TransactionInfo = new TransactionInfoUpdate()
                {
                    TransactionId = context.Order.TransactionInfo.TransactionId,
                    PaymentStatus = await context.Order.CalculateRefundableAmountAsync() == refundRequest.RefundAmount ?
                        PaymentStatus.Refunded : PaymentStatus.PartiallyRefunded,
                },
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
            if (order.Status is MollieOrderStatus.Paid or MollieOrderStatus.Completed)
            {
                return PaymentStatus.Captured;
            }

            if (order.Status is MollieOrderStatus.Canceled or MollieOrderStatus.Expired)
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
