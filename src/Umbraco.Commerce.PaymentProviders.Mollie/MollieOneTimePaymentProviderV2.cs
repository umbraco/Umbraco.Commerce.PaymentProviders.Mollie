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
using Mollie.Api.Models.Capture.Request;
using Mollie.Api.Models.Order.Response;
using Mollie.Api.Models.Payment;
using Mollie.Api.Models.Payment.Request;
using Mollie.Api.Models.Payment.Response;
using Mollie.Api.Models.Refund.Response;
using Umbraco.Commerce.Common.Logging;
using Umbraco.Commerce.Core.Api;
using Umbraco.Commerce.Core.Models;
using Umbraco.Commerce.Core.PaymentProviders;
using Umbraco.Commerce.Extensions;
using MollieAmount = Mollie.Api.Models.Amount;
using MollieLocale = Mollie.Api.Models.Payment.Locale;
using MollieOrderLineType = Mollie.Api.Models.Order.Request.OrderLineDetailsType;
using MolliePaymentStatus = Mollie.Api.Models.Payment.PaymentStatus;
using MollieRefundRequest = Mollie.Api.Models.Refund.Request.RefundRequest;

namespace Umbraco.Commerce.PaymentProviders.Mollie
{
    [PaymentProvider("mollie-onetime-v2")]
    public class MollieOneTimePaymentProviderV2 : PaymentProviderBase<MollieOneTimeSettingsV2>
    {
        private readonly ILogger<MollieOneTimePaymentProviderV2> _logger;
        private const string MollieFailureReasonQueryParam = "mollieFailureReason";

        public MollieOneTimePaymentProviderV2(
            UmbracoCommerceContext ctx,
            ILogger<MollieOneTimePaymentProviderV2> logger)
            : base(ctx)
        {
            _logger = logger;
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

        public override string GetCancelUrl(PaymentProviderContext<MollieOneTimeSettingsV2> ctx)
        {
            ctx.Settings.MustNotBeNull("settings");
            ctx.Settings.CancelUrl.MustNotBeNull("settings.CancelUrl");
            return ctx.Settings.CancelUrl;
        }

        public override string GetErrorUrl(PaymentProviderContext<MollieOneTimeSettingsV2> ctx)
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

        public override string GetContinueUrl(PaymentProviderContext<MollieOneTimeSettingsV2> ctx)
        {
            ctx.Settings.MustNotBeNull("settings");
            ctx.Settings.ContinueUrl.MustNotBeNull("settings.ContinueUrl");

            return ctx.Settings.ContinueUrl;
        }

        public override async Task<PaymentFormResult> GenerateFormAsync(PaymentProviderContext<MollieOneTimeSettingsV2> ctx, CancellationToken cancellationToken = default)
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
            ShippingMethodReadOnly? shippingMethod = ctx.Order.ShippingInfo.ShippingMethodId.HasValue
                ? await Context.Services.ShippingMethodService.GetShippingMethodAsync(ctx.Order.ShippingInfo.ShippingMethodId.Value)
                : null;

            // Adjustments helper
            var processAdjustmentPrice = new Action<Price, List<PaymentLine>, string, int>((price, paymentLines, name, quantity) =>
            {
                bool isDiscount = price.WithTax < 0;
                decimal taxRate = (price.WithTax / price.WithoutTax) - 1;

                paymentLines.Add(new PaymentLine
                {
                    Sku = isDiscount ? "DISCOUNT" : "SURCHARGE",
                    Description = name,
                    Type = isDiscount ? MollieOrderLineType.Discount : MollieOrderLineType.Surcharge,
                    Quantity = quantity,
                    UnitPrice = new MollieAmount(currency.Code, price.WithTax),
                    VatRate = (taxRate * 100).ToString("0.00", CultureInfo.InvariantCulture),
                    VatAmount = new MollieAmount(currency.Code, price.Tax * quantity),
                    TotalAmount = new MollieAmount(currency.Code, price.WithTax * quantity),
                });
            });

            var processPriceAdjustment = new Action<PriceAdjustment, List<PaymentLine>, string, int>((adjustment, paymentLines, namePrefix, quantity) =>
            {
                bool isDiscount = adjustment.Price.WithTax < 0;
                processAdjustmentPrice.Invoke(adjustment.Price, paymentLines, (namePrefix + " " + (isDiscount ? "Discount" : "Fee") + " - " + adjustment.Name).Trim(), quantity);
            });

            var processPriceAdjustments = new Action<IReadOnlyCollection<PriceAdjustment>, List<PaymentLine>, string, int>((adjustments, paymentLines, namePrefix, quantity) =>
            {
                foreach (PriceAdjustment adjustment in adjustments)
                {
                    processPriceAdjustment.Invoke(adjustment, paymentLines, namePrefix, quantity);
                }
            });

            // Create the payment
            using var molliePaymentClient = new PaymentClient(ctx.Settings.TestMode ? ctx.Settings.TestApiKey : ctx.Settings.LiveApiKey);
            var mollieOrderAddress = new PaymentAddressDetails
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

            var molliePaymentLines = new List<PaymentLine>();

            // Process order lines
            foreach (OrderLineReadOnly orderLine in ctx.Order.OrderLines)
            {
                var molliePaymentLine = new PaymentLine
                {
                    Sku = orderLine.Sku,
                    Description = orderLine.Name,
                    Quantity = (int)orderLine.Quantity,
                    UnitPrice = new MollieAmount(currency.Code, orderLine.UnitPrice.WithoutAdjustments.WithTax),
                    VatRate = (orderLine.TaxRate.Value * 100).ToString("0.00", CultureInfo.InvariantCulture),
                    VatAmount = new MollieAmount(currency.Code, orderLine.TotalPrice.Value.Tax),
                    TotalAmount = new MollieAmount(currency.Code, orderLine.TotalPrice.Value.WithTax),
                    Type = !string.IsNullOrWhiteSpace(ctx.Settings.OrderLineProductTypePropertyAlias)
                        ? orderLine.Properties[ctx.Settings.OrderLineProductTypePropertyAlias]
                        : MollieOrderLineType.Physical,
                };

                if (!string.IsNullOrWhiteSpace(ctx.Settings.OrderLineProductCategoryPropertyAlias))
                {
                    molliePaymentLine.Categories = orderLine.Properties[ctx.Settings.OrderLineProductCategoryPropertyAlias].Value.Split(',');
                }

                molliePaymentLines.Add(molliePaymentLine);

                // Because an order line can have sub order lines and various discounts and fees
                // can apply, rather than adding each discount or fee to each order line, we
                // add a single adjustment to the whole primary order line.
                if (orderLine.TotalPrice.TotalAdjustment.WithTax != 0)
                {
                    bool isDiscount = orderLine.TotalPrice.TotalAdjustment.WithTax < 0;
                    string name = (orderLine.Name + " " + (isDiscount ? "Discount" : "Fee")).Trim();

                    processAdjustmentPrice.Invoke(orderLine.TotalPrice.TotalAdjustment, molliePaymentLines, name, 1);
                }
            }

            // Process subtotal price adjustments
            if (ctx.Order.SubtotalPrice.Adjustments.Count > 0)
            {
                processPriceAdjustments.Invoke(ctx.Order.SubtotalPrice.Adjustments, molliePaymentLines, "Subtotal", 1);
            }

            // Process payment fee
            if (ctx.Order.PaymentInfo.TotalPrice.WithoutAdjustments.WithTax > 0)
            {
                string name = $"{paymentMethod.Name} Charge";

                var paymentOrderLine = new PaymentLine
                {
                    Sku = !string.IsNullOrWhiteSpace(paymentMethod.Sku) ? paymentMethod.Sku : "PF001",
                    Description = name,
                    Type = MollieOrderLineType.Surcharge,
                    Quantity = 1,
                    UnitPrice = new MollieAmount(currency.Code, ctx.Order.PaymentInfo.TotalPrice.WithoutAdjustments.WithTax),
                    VatRate = (ctx.Order.PaymentInfo.TaxRate.Value * 100).ToString("0.00", CultureInfo.InvariantCulture),
                    VatAmount = new MollieAmount(currency.Code, ctx.Order.PaymentInfo.TotalPrice.Value.Tax),
                    TotalAmount = new MollieAmount(currency.Code, ctx.Order.PaymentInfo.TotalPrice.Value.WithTax),
                };

                molliePaymentLines.Add(paymentOrderLine);

                if (ctx.Order.PaymentInfo.TotalPrice.Adjustments.Count > 0)
                {
                    processPriceAdjustments.Invoke(ctx.Order.PaymentInfo.TotalPrice.Adjustments, molliePaymentLines, name, 1);
                }
            }

            // Process shipping fee
            if (shippingMethod != null && ctx.Order.ShippingInfo.TotalPrice.WithoutAdjustments.WithTax > 0)
            {
                string name = $"{shippingMethod.Name} Charge";

                var shippingOrderLine = new PaymentLine
                {
                    Sku = !string.IsNullOrWhiteSpace(shippingMethod.Sku) ? shippingMethod.Sku : "SF001",
                    Description = name,
                    Type = MollieOrderLineType.ShippingFee,
                    Quantity = 1,
                    UnitPrice = new MollieAmount(currency.Code, ctx.Order.ShippingInfo.TotalPrice.WithoutAdjustments.WithTax),
                    VatRate = (ctx.Order.ShippingInfo.TaxRate.Value * 100).ToString("0.00", CultureInfo.InvariantCulture),
                    VatAmount = new MollieAmount(currency.Code, ctx.Order.ShippingInfo.TotalPrice.Value.Tax),
                    TotalAmount = new MollieAmount(currency.Code, ctx.Order.ShippingInfo.TotalPrice.Value.WithTax),
                };

                molliePaymentLines.Add(shippingOrderLine);

                if (ctx.Order.ShippingInfo.TotalPrice.Adjustments.Count > 0)
                {
                    processPriceAdjustments.Invoke(ctx.Order.ShippingInfo.TotalPrice.Adjustments, molliePaymentLines, name, 1);
                }
            }

            // Process total price adjustments
            if (ctx.Order.TotalPrice.Adjustments.Count > 0)
            {
                processPriceAdjustments.Invoke(ctx.Order.TotalPrice.Adjustments, molliePaymentLines, "Total", 1);
            }

            // Process gift cards
            var giftCards = ctx.Order.TransactionAmount.Adjustments.OfType<GiftCardAdjustment>().ToList();
            if (giftCards.Count > 0)
            {
                foreach (GiftCardAdjustment giftCard in giftCards)
                {
                    molliePaymentLines.Add(new PaymentLine
                    {
                        Sku = "GIFT_CARD",
                        Description = "Gift Card - " + giftCard.GiftCardCode,
                        Type = MollieOrderLineType.GiftCard,
                        Quantity = 1,
                        UnitPrice = new MollieAmount(currency.Code, giftCard.Amount.Value),
                        VatRate = "0.00",
                        VatAmount = new MollieAmount(currency.Code, 0m),
                        TotalAmount = new MollieAmount(currency.Code, giftCard.Amount.Value),
                    });
                }
            }

            // Process other adjustment types
            var amountAdjustments = ctx.Order.TransactionAmount.Adjustments.Where(x => x is not GiftCardAdjustment).ToList();
            if (amountAdjustments.Count > 0)
            {
                foreach (AmountAdjustment adjustment in amountAdjustments)
                {
                    bool isDiscount = adjustment.Amount.Value < 0;

                    molliePaymentLines.Add(new PaymentLine
                    {
                        Sku = isDiscount ? "DISCOUNT" : "SURCHARGE",
                        Description = "Transaction " + (isDiscount ? "Discount" : "Fee") + " - " + adjustment.Name,
                        Type = isDiscount ? MollieOrderLineType.Discount : MollieOrderLineType.Surcharge,
                        Quantity = 1,
                        UnitPrice = new MollieAmount(currency.Code, adjustment.Amount.Value),
                        VatRate = "0.00",
                        VatAmount = new MollieAmount(currency.Code, 0m),
                        TotalAmount = new MollieAmount(currency.Code, adjustment.Amount.Value),
                    });
                }
            }

            var molliePaymentRequest = new PaymentRequest
            {
                Amount = new MollieAmount(currency.Code.ToUpperInvariant(), ctx.Order.TransactionAmount.Value),
                Description = ctx.Order.OrderNumber,
                Lines = molliePaymentLines,
                Metadata = ctx.Order.GenerateOrderReference(),
                BillingAddress = mollieOrderAddress,
                RedirectUrl = ctx.Urls.CallbackUrl + "?redirect=true", // Explicitly redirect to the callback URL as this will need to do more processing to decide where to redirect to
                WebhookUrl = ctx.Urls.CallbackUrl,
                Locale = !string.IsNullOrWhiteSpace(ctx.Settings.Locale) ? ctx.Settings.Locale : MollieLocale.en_US,
                CaptureMode = ctx.Settings.ManualCapture ? "manual" : null,
            };

            if (!string.IsNullOrWhiteSpace(ctx.Settings.PaymentMethods))
            {
                var paymentMethods = ctx.Settings.PaymentMethods.Split([','], StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList();

                if (paymentMethods.Count == 1)
                {
                    molliePaymentRequest.Method = paymentMethods[0];
                }
                else if (paymentMethods.Count > 1)
                {
                    molliePaymentRequest.Methods = paymentMethods;
                }
            }

            PaymentResponse molliePaymentResult = await molliePaymentClient.CreatePaymentAsync(molliePaymentRequest, false, cancellationToken);
            return new PaymentFormResult
            {
                Form = new PaymentForm(molliePaymentResult.Links.Checkout!.Href, PaymentFormMethod.Get),
                MetaData = new Dictionary<string, string>()
                {
                    { "mollieOrderId", molliePaymentResult.OrderId ?? string.Empty },
                    { "molliePaymentId", molliePaymentResult.Id },
                    { "molliePaymentMethod", molliePaymentResult.Method ?? string.Empty },
                },
            };
        }

        public override async Task<CallbackResult> ProcessCallbackAsync(PaymentProviderContext<MollieOneTimeSettingsV2> context, CancellationToken cancellationToken = default)
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

        private async Task<CallbackResult> ProcessRedirectCallbackAsync(PaymentProviderContext<MollieOneTimeSettingsV2> ctx, CancellationToken cancellationToken = default)
        {
            var result = new CallbackResult();

            string molliePaymentId = await GetMolliePaymentIdAsync(ctx, cancellationToken);
            _logger.Debug("ProcessRedirectCallbackAsync - molliePaymentId: {molliePaymentId}", molliePaymentId);
            using (var molliePaymentClient = new PaymentClient(ctx.Settings.TestMode ? ctx.Settings.TestApiKey : ctx.Settings.LiveApiKey))
            {
                PaymentResponse molliePayment = await molliePaymentClient.GetPaymentAsync(molliePaymentId, includeRemainderDetails: true, cancellationToken: cancellationToken);
                Core.Models.PaymentStatus paymentStatus = await GetPaymentStatusAsync(ctx, molliePayment, cancellationToken);
                if (paymentStatus == Core.Models.PaymentStatus.Error)
                {
                    // Mollie may redirect user here when the payment failed.
                    // We need to redirect user to the UC error page.
                    result.ActionResult = new RedirectResult($"{ctx.Urls.ErrorUrl}?{MollieFailureReasonQueryParam}={molliePayment.StatusReason?.Code}", false);
                }
                else if (paymentStatus == Core.Models.PaymentStatus.Cancelled)
                {
                    result.ActionResult = new RedirectResult(ctx.Urls.CancelUrl, false);
                }
                else
                {
                    // If the order is pending, Mollie won't sent a webhook notification so
                    // we check for this on the return URL and if the order is pending, finalize it
                    // and set it's status to pending before progressing to the confirmation page
                    if (paymentStatus == Core.Models.PaymentStatus.PendingExternalSystem)
                    {
                        result.TransactionInfo = new TransactionInfo
                        {
                            AmountAuthorized = decimal.Parse(molliePayment.Amount.Value, CultureInfo.InvariantCulture),
                            TransactionFee = 0m,
                            TransactionId = molliePaymentId,
                            PaymentStatus = Core.Models.PaymentStatus.PendingExternalSystem,
                        };
                    }

                    result.ActionResult = new RedirectResult(ctx.Urls.ContinueUrl, false);
                }
            }

            return result;
        }

        private async Task<CallbackResult> ProcessWebhookCallbackAsync(PaymentProviderContext<MollieOneTimeSettingsV2> ctx, CancellationToken cancellationToken = default)
        {
            IFormCollection formData = await ctx.HttpContext.Request.ReadFormAsync(cancellationToken);
            string? id = formData["id"];

            string molliePaymentId = await GetMolliePaymentIdAsync(ctx, cancellationToken);

            // Validate the ID from the webhook matches the order's molliePaymentId property
            if (id != molliePaymentId)
            {
                return CallbackResult.Ok();
            }


            using var molliePaymentClient = new PaymentClient(ctx.Settings.TestMode ? ctx.Settings.TestApiKey : ctx.Settings.LiveApiKey);
            PaymentResponse molliePayment = await molliePaymentClient.GetPaymentAsync(molliePaymentId, cancellationToken: cancellationToken);
            Core.Models.PaymentStatus paymentStatus = await GetPaymentStatusAsync(ctx, molliePayment, cancellationToken);

            _logger.Debug("ProcessWebhookCallbackAsync - molliePaymentId: {molliePaymentId}; molliePayment.Status: {molliePaymentStatus};", molliePaymentId, molliePayment.Status);

            // Mollie sends cancelled notifications for unfinalized orders so we need to ensure that
            // we only cancel orders that are authorized
            if (paymentStatus == Core.Models.PaymentStatus.Cancelled && ctx.Order.TransactionInfo.PaymentStatus != Core.Models.PaymentStatus.Authorized)
            {
                return CallbackResult.Ok();
            }

            return CallbackResult.Ok(
                new TransactionInfo
                {
                    AmountAuthorized = decimal.Parse(molliePayment.Amount.Value, CultureInfo.InvariantCulture),
                    TransactionFee = 0m,
                    TransactionId = molliePaymentId,
                    PaymentStatus = paymentStatus,
                },
                new Dictionary<string, string>
                {
                    { "molliePaymentMethod", molliePayment.Method ?? string.Empty },
                    { "molliePaymentId", molliePayment.Id },
                });
        }

        public override async Task<ApiResult> FetchPaymentStatusAsync(PaymentProviderContext<MollieOneTimeSettingsV2> context, CancellationToken cancellationToken = default)
        {
            string molliePaymentId = await GetMolliePaymentIdAsync(context, cancellationToken);
            using var molliePaymentClient = new PaymentClient(context.Settings.TestMode ? context.Settings.TestApiKey : context.Settings.LiveApiKey);
            PaymentResponse molliePayment = await molliePaymentClient.GetPaymentAsync(molliePaymentId, cancellationToken: cancellationToken);

            return new ApiResult
            {
                TransactionInfo = new TransactionInfoUpdate()
                {
                    TransactionId = context.Order.TransactionInfo.TransactionId,
                    PaymentStatus = await GetPaymentStatusAsync(context, molliePayment, cancellationToken),
                },
            };
        }

        public override async Task<ApiResult> CancelPaymentAsync(PaymentProviderContext<MollieOneTimeSettingsV2> ctx, CancellationToken cancellationToken = default)
        {
            string molliePaymentId = await GetMolliePaymentIdAsync(ctx, cancellationToken);
            using var molliePaymentClient = new PaymentClient(ctx.Settings.TestMode ? ctx.Settings.TestApiKey : ctx.Settings.LiveApiKey);
            await molliePaymentClient.CancelPaymentAsync(molliePaymentId, cancellationToken: cancellationToken);

            PaymentResponse mollieOrder = await molliePaymentClient.GetPaymentAsync(molliePaymentId, cancellationToken: cancellationToken);

            return new ApiResult
            {
                TransactionInfo = new TransactionInfoUpdate()
                {
                    TransactionId = ctx.Order.TransactionInfo.TransactionId,
                    PaymentStatus = await GetPaymentStatusAsync(ctx, mollieOrder, cancellationToken),
                },
            };
        }

        public override async Task<ApiResult?> RefundPaymentAsync(
            PaymentProviderContext<MollieOneTimeSettingsV2> context,
            PaymentProviderOrderRefundRequest refundRequest,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(refundRequest);

            string molliePaymentId = await GetMolliePaymentIdAsync(context, cancellationToken);
            CurrencyReadOnly currency = await Context.Services.CurrencyService.GetCurrencyAsync(context.Order.CurrencyId);
            using var mollieRefundClient = new RefundClient(context.Settings.TestMode ? context.Settings.TestApiKey : context.Settings.LiveApiKey);
            RefundResponse refundResponse = await mollieRefundClient.CreatePaymentRefundAsync(
                molliePaymentId,
                new MollieRefundRequest
                {
                    Amount = new MollieAmount(currency.Code, refundRequest.RefundAmount),
                },
                cancellationToken);

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
                        Core.Models.PaymentStatus.Refunded : Core.Models.PaymentStatus.PartiallyRefunded,
                },
            };
        }

        /// <summary>
        /// Look for the mollie payment id from order properties or fetch from mollie order api.
        /// </summary>
        /// <param name="context"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        /// <exception cref="MolliePaymentProviderGeneralException">Throws when the mollie payment id could not be found.</exception>
        private static async Task<string> GetMolliePaymentIdAsync(PaymentProviderContext<MollieOneTimeSettingsV2> context, CancellationToken cancellationToken = default)
        {
            string? molliePaymentId = context.Order.Properties["molliePaymentId"]?.Value;
            if (!string.IsNullOrEmpty(molliePaymentId))
            {
                return molliePaymentId;
            }

            string mollieOrderId = context.Order.Properties["mollieOrderId"]?.Value
                ?? throw new MolliePaymentProviderGeneralException($"Mollie Order Id could not be found on the order number '{context.Order.OrderNumber}'.");
#pragma warning disable CS0618 // Type or member is obsolete
            using var mollieOrderClient = new OrderClient(context.Settings.TestMode ? context.Settings.TestApiKey : context.Settings.LiveApiKey);
#pragma warning restore CS0618 // Type or member is obsolete
            OrderResponse mollieOrder = await mollieOrderClient.GetOrderAsync(mollieOrderId, true, true, cancellationToken: cancellationToken);
            molliePaymentId = (mollieOrder.Embedded?.Payments ?? []).FirstOrDefault(x => x.Status == MolliePaymentStatus.Paid)?.Id;

            if (!string.IsNullOrEmpty(molliePaymentId))
            {
                return molliePaymentId;
            }

            throw new MolliePaymentProviderGeneralException($"Mollie Payment Id could not be found on the order number '{context.Order.OrderNumber}'.");
        }

        public override async Task<ApiResult> CapturePaymentAsync(PaymentProviderContext<MollieOneTimeSettingsV2> ctx, CancellationToken cancellationToken = default)
        {
            string molliePaymentId = await GetMolliePaymentIdAsync(ctx, cancellationToken);
            using var mollieCaptureApi = new CaptureClient(ctx.Settings.TestMode ? ctx.Settings.TestApiKey : ctx.Settings.LiveApiKey);
            await mollieCaptureApi.CreateCapture(
                molliePaymentId,
                new CaptureRequest(),
                cancellationToken);

            using var molliePaymentClient = new PaymentClient(ctx.Settings.TestMode ? ctx.Settings.TestApiKey : ctx.Settings.LiveApiKey);
            PaymentResponse molliePayment = await molliePaymentClient.GetPaymentAsync(molliePaymentId, cancellationToken: cancellationToken);

            return new ApiResult
            {
                TransactionInfo = new TransactionInfoUpdate()
                {
                    TransactionId = ctx.Order.TransactionInfo.TransactionId,
                    PaymentStatus = await GetPaymentStatusAsync(ctx, molliePayment, cancellationToken),
                },
            };
        }

        private static async Task<Core.Models.PaymentStatus> GetPaymentStatusAsync(PaymentProviderContext<MollieOneTimeSettingsV2> ctx, PaymentResponse molliePayment, CancellationToken cancellationToken = default)
        {
            if (molliePayment.AmountRefunded != null && molliePayment.AmountRefunded > 0)
            {
                // The order is refunded if the total refunded amount is
                // greater than or equal to the original amount of the order
                return await ctx.Order.CalculateRefundableAmountAsync() <= molliePayment.AmountRefunded ?
                        Core.Models.PaymentStatus.Refunded : Core.Models.PaymentStatus.PartiallyRefunded;
            }

            return molliePayment.Status switch
            {
                MolliePaymentStatus.Authorized => Core.Models.PaymentStatus.Authorized,
                MolliePaymentStatus.Canceled or MolliePaymentStatus.Expired => Core.Models.PaymentStatus.Cancelled,
                MolliePaymentStatus.Paid => Core.Models.PaymentStatus.Captured,
                MolliePaymentStatus.Failed => Core.Models.PaymentStatus.Error,
                _ => Core.Models.PaymentStatus.PendingExternalSystem,
            };
        }
    }
}
