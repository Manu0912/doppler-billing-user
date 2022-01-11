using System;
using Doppler.BillingUser.DopplerSecurity;
using Doppler.BillingUser.Model;
using Doppler.BillingUser.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using Doppler.BillingUser.ExternalServices.AccountPlansApi;
using FluentValidation;
using Doppler.BillingUser.Enums;
using Doppler.BillingUser.ExternalServices.FirstData;
using Doppler.BillingUser.ExternalServices.Sap;
using Doppler.BillingUser.Encryption;
using System.Linq;
using Doppler.BillingUser.ExternalServices.Slack;
using Microsoft.Extensions.Options;
using Doppler.BillingUser.ExternalServices.EmailSender;
using Doppler.BillingUser.Utils;

namespace Doppler.BillingUser.Controllers
{
    [Authorize]
    [ApiController]
    public class BillingController
    {
        private readonly ILogger _logger;
        private readonly IBillingRepository _billingRepository;
        private readonly IUserRepository _userRepository;
        private readonly IValidator<BillingInformation> _billingInformationValidator;
        private readonly IAccountPlansService _accountPlansService;
        private readonly IValidator<AgreementInformation> _agreementInformationValidator;
        private readonly IPaymentGateway _paymentGateway;
        private readonly IEmailSender _emailSender;
        private readonly IOptions<EmailNotificationsConfiguration> _emailSettings;
        private readonly ISapService _sapService;
        private readonly IEncryptionService _encryptionService;
        private readonly IOptions<SapSettings> _sapSettings;
        private readonly IPromotionRepository _promotionRepository;
        private readonly ISlackService _slackService;
        private const int CurrencyTypeUsd = 0;

        public BillingController(
            ILogger<BillingController> logger,
            IBillingRepository billingRepository,
            IUserRepository userRepository,
            IValidator<BillingInformation> billingInformationValidator,
            IValidator<AgreementInformation> agreementInformationValidator,
            IAccountPlansService accountPlansService,
            IPaymentGateway paymentGateway,
            ISapService sapService,
            IEncryptionService encryptionService,
            IOptions<SapSettings> sapSettings,
            IPromotionRepository promotionRepository,
            ISlackService slackService,
            IEmailSender emailSender,
            IOptions<EmailNotificationsConfiguration> emailSettings)
        {
            _logger = logger;
            _billingRepository = billingRepository;
            _userRepository = userRepository;
            _billingInformationValidator = billingInformationValidator;
            _agreementInformationValidator = agreementInformationValidator;
            _accountPlansService = accountPlansService;
            _paymentGateway = paymentGateway;
            _sapService = sapService;
            _emailSender = emailSender;
            _emailSettings = emailSettings;
            _encryptionService = encryptionService;
            _sapSettings = sapSettings;
            _promotionRepository = promotionRepository;
            _slackService = slackService;
        }

        [Authorize(Policies.OWN_RESOURCE_OR_SUPERUSER)]
        [HttpGet("/accounts/{accountName}/billing-information")]
        public async Task<IActionResult> GetBillingInformation(string accountName)
        {
            var billingInformation = await _billingRepository.GetBillingInformation(accountName);

            if (billingInformation == null)
            {
                return new NotFoundResult();
            }

            return new OkObjectResult(billingInformation);
        }

        [Authorize(Policies.OWN_RESOURCE_OR_SUPERUSER)]
        [HttpPut("/accounts/{accountname}/billing-information")]
        public async Task<IActionResult> UpdateBillingInformation(string accountname, [FromBody] BillingInformation billingInformation)
        {
            var results = await _billingInformationValidator.ValidateAsync(billingInformation);
            if (!results.IsValid)
            {
                return new BadRequestObjectResult(results.ToString("-"));
            }

            await _billingRepository.UpdateBillingInformation(accountname, billingInformation);

            return new OkObjectResult("Successfully");
        }

        [Authorize(Policies.OWN_RESOURCE_OR_SUPERUSER)]
        [HttpGet("/accounts/{accountname}/billing-information/invoice-recipients")]
        public async Task<IActionResult> GetInvoiceRecipients(string accountname)
        {
            var result = await _billingRepository.GetInvoiceRecipients(accountname);

            if (result == null)
            {
                return new NotFoundResult();
            }

            return new OkObjectResult(result);
        }

        [Authorize(Policies.OWN_RESOURCE_OR_SUPERUSER)]
        [HttpPut("/accounts/{accountname}/billing-information/invoice-recipients")]
        public async Task<IActionResult> UpdateInvoiceRecipients(string accountname, [FromBody] InvoiceRecipients invoiceRecipients)
        {
            await _billingRepository.UpdateInvoiceRecipients(accountname, invoiceRecipients.Recipients, invoiceRecipients.PlanId);

            return new OkObjectResult("Successfully");
        }

        [Authorize(Policies.OWN_RESOURCE_OR_SUPERUSER)]
        [HttpGet("/accounts/{accountname}/payment-methods/current")]
        public async Task<IActionResult> GetCurrentPaymentMethod(string accountname)
        {
            _logger.LogDebug("Get current payment method.");

            var currentPaymentMethod = await _billingRepository.GetCurrentPaymentMethod(accountname);

            if (currentPaymentMethod == null)
            {
                return new NotFoundResult();
            }

            return new OkObjectResult(currentPaymentMethod);
        }

        [Authorize(Policies.OWN_RESOURCE_OR_SUPERUSER)]
        [HttpPut("/accounts/{accountname}/payment-methods/current")]
        public async Task<IActionResult> UpdateCurrentPaymentMethod(string accountname, [FromBody] PaymentMethod paymentMethod)
        {
            _logger.LogDebug("Update current payment method.");

            var isSuccess = await _billingRepository.UpdateCurrentPaymentMethod(accountname, paymentMethod);

            if (!isSuccess)
            {
                var messageError = $"Failed at updating payment method for user {accountname}, Invalid Credit Card";
                _logger.LogError(messageError);
                await _slackService.SendNotification(messageError);
                return new BadRequestObjectResult("Invalid Credit Card");
            }

            return new OkObjectResult("Successfully");
        }

        [Authorize(Policies.OWN_RESOURCE_OR_SUPERUSER)]
        [HttpGet("/accounts/{accountname}/plans/current")]
        public async Task<IActionResult> GetCurrentPlan(string accountname)
        {
            _logger.LogDebug("Get current plan.");

            var currentPlan = await _billingRepository.GetCurrentPlan(accountname);

            if (currentPlan == null)
            {
                return new NotFoundResult();
            }

            return new OkObjectResult(currentPlan);
        }

        [Authorize(Policies.OWN_RESOURCE_OR_SUPERUSER)]
        [HttpPost("/accounts/{accountname}/agreements")]
        public async Task<IActionResult> CreateAgreement([FromRoute] string accountname, [FromBody] AgreementInformation agreementInformation)
        {
            try
            {
                var results = await _agreementInformationValidator.ValidateAsync(agreementInformation);
                if (!results.IsValid)
                {
                    var messageError = $"Failed at creating new agreement for user {accountname}, Validation error {results.ToString("-")}";
                    _logger.LogError(messageError);
                    await _slackService.SendNotification(messageError);
                    return new BadRequestObjectResult(results.ToString("-"));
                }

                var user = await _userRepository.GetUserBillingInformation(accountname);
                if (user == null)
                {
                    var messageError = $"Failed at creating new agreement for user {accountname}, Invalid user";
                    _logger.LogError(messageError);
                    await _slackService.SendNotification(messageError);
                    return new NotFoundObjectResult("Invalid user");
                }

                if (user.PaymentMethod != PaymentMethodEnum.CC)
                {
                    var messageError = $"Failed at creating new agreement for user {accountname}, Invalid payment method {user.PaymentMethod}";
                    _logger.LogError(messageError);
                    await _slackService.SendNotification(messageError);
                    return new BadRequestObjectResult("Invalid payment method");
                }

                var currentPlan = await _userRepository.GetUserCurrentTypePlan(user.IdUser);
                if (currentPlan != null)
                {
                    var messageError = $"Failed at creating new agreement for user {accountname}, Invalid user type (only free users) {currentPlan.IdUserType}";
                    _logger.LogError(messageError);
                    await _slackService.SendNotification(messageError);
                    return new BadRequestObjectResult("Invalid user type (only free users)");
                }

                var newPlan = await _userRepository.GetUserNewTypePlan(agreementInformation.PlanId);
                if (newPlan == null)
                {
                    var messageError = $"Failed at creating new agreement for user {accountname}, Invalid selected plan {agreementInformation.PlanId}";
                    _logger.LogError(messageError);
                    await _slackService.SendNotification(messageError);
                    return new BadRequestObjectResult("Invalid selected plan");
                }

                if (newPlan.IdUserType != UserTypeEnum.INDIVIDUAL)
                {
                    var messageError = $"Failed at creating new agreement for user {accountname}, invalid selected plan type {newPlan.IdUserType}";
                    _logger.LogError(messageError);
                    await _slackService.SendNotification(messageError);
                    return new BadRequestObjectResult("Invalid selected plan type");
                }

                var isValidTotal = await _accountPlansService.IsValidTotal(accountname, agreementInformation);

                if (!isValidTotal)
                {
                    var messageError = $"Failed at creating new agreement for user {accountname}, Total of agreement is not valid";
                    _logger.LogError(messageError);
                    await _slackService.SendNotification(messageError);
                    return new BadRequestObjectResult("Total of agreement is not valid");
                }

                Promotion promotion = null;
                if (!string.IsNullOrEmpty(agreementInformation.Promocode))
                {
                    promotion = await _accountPlansService.GetValidPromotionByCode(agreementInformation.Promocode, agreementInformation.PlanId);
                }

                int invoiceId = 0;
                string authorizationNumber = string.Empty;
                CreditCard encryptedCreditCard = null;
                if (agreementInformation.Total.GetValueOrDefault() > 0)
                {
                    encryptedCreditCard = await _userRepository.GetEncryptedCreditCard(accountname);
                    if (encryptedCreditCard == null)
                    {
                        var messageError = $"Failed at creating new agreement for user {accountname}, missing credit card information";
                        _logger.LogError(messageError);
                        await _slackService.SendNotification(messageError);
                        return new ObjectResult("User credit card missing")
                        {
                            StatusCode = 500
                        };
                    }

                    // TODO: Deal with first data exceptions.
                    authorizationNumber = await _paymentGateway.CreateCreditCardPayment(agreementInformation.Total.GetValueOrDefault(), encryptedCreditCard, user.IdUser);
                    invoiceId = await _billingRepository.CreateAccountingEntriesAsync(agreementInformation, encryptedCreditCard, user.IdUser, authorizationNumber);
                }

                var billingCreditId = await _billingRepository.CreateBillingCreditAsync(agreementInformation, user, newPlan, promotion);

                user.IdCurrentBillingCredit = billingCreditId;
                user.OriginInbound = agreementInformation.OriginInbound;
                user.UpgradePending = false;
                await _userRepository.UpdateUserBillingCredit(user);

                var partialBalance = await _userRepository.GetAvailableCredit(user.IdUser);
                await _billingRepository.CreateMovementCreditAsync(billingCreditId, partialBalance, user, newPlan);

                if (promotion != null)
                    await _promotionRepository.IncrementUsedTimes(promotion);

                if (agreementInformation.Total.GetValueOrDefault() > 0)
                {
                    await _sapService.SendBillingToSap(
                        await MapBillingToSapAsync(encryptedCreditCard, currentPlan, newPlan, authorizationNumber,
                            invoiceId, billingCreditId),
                        accountname);
                }

                User userInformation = await _userRepository.GetUserInformation(accountname);
                var template = _emailSettings.Value.CreditsApprovedTemplateId[userInformation.Language ?? "en"];

                await _emailSender.SafeSendWithTemplateAsync(
                        templateId: template,
                        templateModel: new
                        {
                            urlImagesBase = _emailSettings.Value.UrlEmailImagesBase,
                            firstName = userInformation.FirstName,
                            isIndividualPlan = newPlan.IdUserType == UserTypeEnum.INDIVIDUAL,
                            isMonthlyPlan = newPlan.IdUserType == UserTypeEnum.MONTHLY,
                            isSubscribersPlan = newPlan.IdUserType == UserTypeEnum.SUBSCRIBERS,
                            creditsQty = newPlan.EmailQty,
                            subscribersQty = newPlan.Subscribers,
                            amount = newPlan.Fee,
                            isPaymentMethodCC = user.PaymentMethod == PaymentMethodEnum.CC,
                            isPaymentMethodMP = user.PaymentMethod == PaymentMethodEnum.MP,
                            isPaymentMethodTransf = user.PaymentMethod == PaymentMethodEnum.TRANSF,
                            availableCreditsQty = partialBalance + newPlan.EmailQty + (promotion != null ? promotion.ExtraCredits ?? 0 : 0),
                            year = DateTime.UtcNow.Year
                        },
                        to: new[] { accountname });

                var templateAdmin = _emailSettings.Value.CreditsApprovedAdminTemplateId;

                await _emailSender.SafeSendWithTemplateAsync(
                        templateId: templateAdmin,
                        templateModel: new
                        {
                            urlImagesBase = _emailSettings.Value.UrlEmailImagesBase,
                            user = accountname,
                            client = $"{userInformation.FirstName} {userInformation.LastName}",
                            address = userInformation.Address,
                            phone = userInformation.PhoneNumber,
                            company = userInformation.Company,
                            city = userInformation.CityName,
                            state = userInformation.BillingStateName,
                            zipCode = userInformation.ZipCode,
                            language = userInformation.Language,
                            country = userInformation.BillingCountryName,
                            vendor = userInformation.Vendor,
                            promotionCode = agreementInformation.Promocode,
                            promotionCodeDiscount = promotion?.DiscountPlanFee,
                            promotionCodeExtraCredits = promotion?.ExtraCredits,
                            razonSocial = userInformation.RazonSocial,
                            cuit = userInformation.CUIT,
                            isConsumerCF = userInformation.IdConsumerType == (int)ConsumerTypeEnum.CF,
                            isConsumerRFC = userInformation.IdConsumerType == (int)ConsumerTypeEnum.RFC,
                            isConsumerRI = userInformation.IdConsumerType == (int)ConsumerTypeEnum.RI,
                            isCfdiUseG03 = user.CFDIUse == "G03",
                            isCfdiUseP01 = user.CFDIUse == "P01",
                            isPaymentTypePPD = user.PaymentType == "PPD",
                            isPaymentTypePUE = user.PaymentType == "PUE",
                            isPaymentWayCash = user.PaymentWay == "CASH",
                            isPaymentWayCheck = user.PaymentWay == "CHECK",
                            isPaymentWayTransfer = user.PaymentWay == "TRANSFER",
                            bankName = user.BankName,
                            bankAccount = user.BankAccount,
                            billingEmails = userInformation.BillingEmails,
                            //userMessage = user.ExclusiveMessage, //TODO: set when the property is set in BilligCredit
                            isIndividualPlan = newPlan.IdUserType == UserTypeEnum.INDIVIDUAL,
                            isMonthlyPlan = newPlan.IdUserType == UserTypeEnum.MONTHLY,
                            isSubscribersPlan = newPlan.IdUserType == UserTypeEnum.SUBSCRIBERS,
                            creditsQty = newPlan.EmailQty,
                            subscribersQty = newPlan.Subscribers,
                            amount = newPlan.Fee,
                            isPaymentMethodCC = user.PaymentMethod == PaymentMethodEnum.CC,
                            isPaymentMethodMP = user.PaymentMethod == PaymentMethodEnum.MP,
                            isPaymentMethodTransf = user.PaymentMethod == PaymentMethodEnum.TRANSF,
                            year = DateTime.UtcNow.Year
                        },
                        to: new[] { _emailSettings.Value.AdminEmail });

                var message = $"Successful at creating a new agreement for: User: {accountname} - Plan: {agreementInformation.PlanId}";
                await _slackService.SendNotification(message + (!string.IsNullOrEmpty(agreementInformation.Promocode) ? $" - Promocode {agreementInformation.Promocode}" : string.Empty));
                return new OkObjectResult("Successfully");
            }
            catch (Exception e)
            {
                var messageError = $"Failed at creating new agreement for user {accountname} with exception {e.Message}";
                _logger.LogError(e, messageError);
                await _slackService.SendNotification(messageError);
                return new ObjectResult("Failed at creating new agreement")
                {
                    StatusCode = 500
                };
            }
        }

        private async Task<SapBillingDto> MapBillingToSapAsync(CreditCard creditCard, UserTypePlanInformation currentUserPlan, UserTypePlanInformation newUserPlan, string authorizationNumber, int invoidId, int billingCreditId)
        {
            var billingCredit = await _billingRepository.GetBillingCredit(billingCreditId);
            var cardNumber = _encryptionService.DecryptAES256(creditCard.Number);

            var sapBilling = new SapBillingDto
            {
                Id = billingCredit.IdUser,
                CreditsOrSubscribersQuantity = billingCredit.CreditsQty.GetValueOrDefault(),
                IsCustomPlan = new[] { 0, 9, 17 }.Contains(billingCredit.IdUserTypePlan),
                IsPlanUpgrade = true, // TODO: Check when the other types of purchases are implemented.
                Currency = CurrencyTypeUsd,
                Periodicity = null,
                PeriodMonth = billingCredit.Date.Month,
                PeriodYear = billingCredit.Date.Year,
                PlanFee = billingCredit.PlanFee,
                Discount = billingCredit.DiscountPlanFee,
                ExtraEmailsPeriodMonth = billingCredit.Date.Month,
                ExtraEmailsPeriodYear = billingCredit.Date.Year,
                ExtraEmailsFee = 0,
                IsFirstPurchase = currentUserPlan == null,
                PlanType = (int)newUserPlan.IdUserType,
                CardHolder = _encryptionService.DecryptAES256(creditCard.HolderName),
                CardType = billingCredit.CCIdentificationType,
                CardNumber = cardNumber[^4..],
                CardErrorCode = "100",
                CardErrorDetail = "Successfully approved",
                TransactionApproved = true,
                TransferReference = authorizationNumber,
                InvoiceId = invoidId,
                PaymentDate = billingCredit.Date.ToHourOffset(_sapSettings.Value.TimeZoneOffset),
                InvoiceDate = billingCredit.Date.ToHourOffset(_sapSettings.Value.TimeZoneOffset),
                BillingSystemId = billingCredit.IdResponsabileBilling
            };

            return sapBilling;
        }
    }
}
