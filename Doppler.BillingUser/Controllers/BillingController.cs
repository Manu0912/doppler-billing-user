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
using System;

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

        public BillingController(
            ILogger<BillingController> logger,
            IBillingRepository billingRepository,
            IUserRepository userRepository,
            IValidator<BillingInformation> billingInformationValidator,
            IValidator<AgreementInformation> agreementInformationValidator,
            IAccountPlansService accountPlansService,
            IPaymentGateway paymentGateway)
        {
            _logger = logger;
            _billingRepository = billingRepository;
            _userRepository = userRepository;
            _billingInformationValidator = billingInformationValidator;
            _agreementInformationValidator = agreementInformationValidator;
            _accountPlansService = accountPlansService;
            _paymentGateway = paymentGateway;
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
            var results = await _agreementInformationValidator.ValidateAsync(agreementInformation);
            if (!results.IsValid)
            {
                return new BadRequestObjectResult(results.ToString("-"));
            }

            var user = await _userRepository.GetUserBillingInformation(accountname);
            if (user == null)
            {
                return new NotFoundObjectResult("Invalid user");
            }

            if (user.PaymentMethod != PaymentMethodEnum.CC)
            {
                return new BadRequestObjectResult("Invalid payment method");
            }

            var isValidTotal = await _accountPlansService.IsValidTotal(accountname, agreementInformation);

            if (!isValidTotal)
            {
                return new BadRequestObjectResult("Total of agreement is not valid");
            }

            var currentPlan = await _userRepository.GetUserCurrentTypePlan(user.IdUser);
            if (currentPlan != null)
            {
                return new BadRequestObjectResult("Invalid user type (only free users)");
            }

            var newPlan = await _userRepository.GetUserNewTypePlan(agreementInformation.PlanId);
            if (newPlan == null)
            {
                return new BadRequestObjectResult("Invalid selected plan");
            }

            if (newPlan.IdUserType != UserTypeEnum.INDIVIDUAL)
            {
                return new BadRequestObjectResult("Invalid selected plan type");
            }

            if (agreementInformation.Total.GetValueOrDefault() > 0)
            {
                var encryptedCreditCard = await _userRepository.GetEncryptedCreditCard(accountname);
                if (encryptedCreditCard == null)
                {
                    return new ObjectResult("User credit card missing")
                    {
                        StatusCode = 500
                    };
                }

                // TODO: Deal with first data exceptions.
                var authorizationNumber = await _paymentGateway.CreateCreditCardPayment(agreementInformation.Total.GetValueOrDefault(), encryptedCreditCard, user.IdUser);
                await _billingRepository.CreateAccountingEntriesAsync(agreementInformation, encryptedCreditCard, user.IdUser, authorizationNumber);
            }

            var billingCreditId = await _billingRepository.CreateBillingCreditAsync(agreementInformation, user, newPlan);

            user.IdCurrentBillingCredit = billingCreditId;
            await _userRepository.UpdateUserBillingCredit(user);

            if (agreementInformation.Total.GetValueOrDefault() > 0)
            {
                var partialBalance = await _userRepository.GetAvailableCredit(user.IdUser);
                await _billingRepository.CreateMovementCreditAsync(billingCreditId, partialBalance, user, newPlan);
            }

            // TODO: SEND NOTIFICATIONS
            // TODO: create invoice in SAP

            return new OkObjectResult("Successfully");
        }
    }
}
