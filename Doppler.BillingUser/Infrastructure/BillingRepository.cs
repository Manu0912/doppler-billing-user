using Dapper;
using Doppler.BillingUser.Encryption;
using Doppler.BillingUser.Enums;
using Doppler.BillingUser.ExternalServices.FirstData;
using Doppler.BillingUser.ExternalServices.Sap;
using Doppler.BillingUser.Model;
using Doppler.BillingUser.Utils;
using Newtonsoft.Json;
using System;
using System.Data;
using System.Linq;
using System.Threading.Tasks;

namespace Doppler.BillingUser.Infrastructure
{
    public class BillingRepository : IBillingRepository
    {
        private readonly IDatabaseConnectionFactory _connectionFactory;
        private readonly IEncryptionService _encryptionService;
        private readonly IPaymentGateway _paymentGateway;
        private readonly ISapService _sapService;

        public BillingRepository(IDatabaseConnectionFactory connectionFactory,
            IEncryptionService encryptionService,
            IPaymentGateway paymentGateway,
            ISapService sapService)
        {
            _connectionFactory = connectionFactory;
            _encryptionService = encryptionService;
            _paymentGateway = paymentGateway;
            _sapService = sapService;
        }
        public async Task<BillingInformation> GetBillingInformation(string email)
        {
            using (IDbConnection connection = await _connectionFactory.GetConnection())
            {
                var results = await connection.QueryAsync<BillingInformation>(@"
SELECT
    U.BillingFirstName AS Firstname,
    U.BillingLastName AS Lastname,
    U.BillingAddress AS Address,
    U.BillingCity AS City,
    isnull(S.StateCode, '') AS Province,
    isnull(CO.Code, '') AS Country,
    U.BillingZip AS ZipCode,
    U.BillingPhone AS Phone
FROM
    [User] U
    LEFT JOIN [State] S ON U.IdBillingState = S.IdState
    LEFT JOIN [Country] CO ON S.IdCountry = CO.IdCountry
WHERE
    U.Email = @email",
                    new { email });
                return results.FirstOrDefault();
            }
        }

        public async Task UpdateBillingInformation(string accountName, BillingInformation billingInformation)
        {
            using var connection = await _connectionFactory.GetConnection();

            await connection.ExecuteAsync(@"
UPDATE [User] SET
    [BillingFirstName] = @firstname,
    [BillingLastName] = @lastname,
    [BillingAddress] = @address,
    [BillingCity] = @city,
    [IdBillingState] = (SELECT IdState FROM [State] WHERE StateCode = @idBillingState),
    [BillingPhone] = @phoneNumber,
    [BillingZip] = @zipCode
WHERE
    Email = @email;",
                new
                {
                    @firstname = billingInformation.Firstname,
                    @lastname = billingInformation.Lastname,
                    @address = billingInformation.Address,
                    @city = billingInformation.City,
                    @idBillingState = billingInformation.Province,
                    @phoneNumber = billingInformation.Phone,
                    @zipCode = billingInformation.ZipCode,
                    @email = accountName
                });
        }

        public async Task<PaymentMethod> GetCurrentPaymentMethod(string username)
        {
            using var connection = await _connectionFactory.GetConnection();

            var result = await connection.QueryFirstOrDefaultAsync<PaymentMethod>(@"
SELECT
    B.CCHolderFullName,
    B.CCNumber,
    B.CCExpMonth,
    B.CCExpYear,
    B.CCVerification,
    C.[Description] AS CCType,
    P.PaymentMethodName AS PaymentMethodName,
    D.MonthPlan AS RenewalMonth,
    B.RazonSocial,
    B.IdConsumerType,
    B.CCIdentificationType AS IdentificationType,
    ISNULL(B.CUIT, B.CCIdentificationNumber) AS IdentificationNumber
FROM
    [BillingCredits] B
LEFT JOIN
    [PaymentMethods] P ON P.IdPaymentMethod = B.IdPaymentMethod
LEFT JOIN
    [CreditCardTypes] C ON C.IdCCType = B.IdCCType
LEFT JOIN
    [DiscountXPlan] D ON D.IdDiscountPlan = B.IdDiscountPlan
WHERE
    B.IdUser = (SELECT IdUser FROM [User] WHERE Email = @email) ORDER BY [Date] DESC;",
                new
                {
                    @email = username
                });

            result.IdConsumerType = ConsumerTypeHelper.GetConsumerType(result.IdConsumerType);

            if (result is not { PaymentMethodName: "CC" or "MP" })
                return result;

            result.CCHolderFullName = _encryptionService.DecryptAES256(result.CCHolderFullName);
            result.CCNumber = CreditCardHelper.ObfuscateNumber(_encryptionService.DecryptAES256(result.CCNumber));
            result.CCVerification = CreditCardHelper.ObfuscateVerificationCode(_encryptionService.DecryptAES256(result.CCVerification));

            return result;
        }

        public async Task<bool> UpdateCurrentPaymentMethod(string accountName, PaymentMethod paymentMethod)
        {
            using var connection = await _connectionFactory.GetConnection();

            var userId = await connection.QueryFirstOrDefaultAsync<int>(@"
SELECT IdUser
FROM [User]
WHERE Email = @email;",
                new
                {
                    @email = accountName
                });

            if (paymentMethod.PaymentMethodName == PaymentMethodEnum.CC.ToString())
            {

                var creditCard = new CreditCard()
                {
                    Number = _encryptionService.EncryptAES256(paymentMethod.CCNumber),
                    HolderName = _encryptionService.EncryptAES256(paymentMethod.CCHolderFullName),
                    ExpirationMonth = int.Parse(paymentMethod.CCExpMonth),
                    ExpirationYear = int.Parse(paymentMethod.CCExpYear),
                    Code = _encryptionService.EncryptAES256(paymentMethod.CCVerification)
                };

                //Validate CC
                var validCC = await _paymentGateway.IsValidCreditCard(creditCard, userId);
                if (!validCC)
                {
                    return false;
                }

                //Create Billing Credits in DB with CC information
                await UpdateUserPaymentMethod(userId, paymentMethod);

                //Send BP to SAP
                await SendUserDataToSap(userId, paymentMethod);

                return true;
            }
            else if (paymentMethod.PaymentMethodName == PaymentMethodEnum.MP.ToString())
            {
                return true;
            }
            else if (paymentMethod.PaymentMethodName == PaymentMethodEnum.TRANSF.ToString())
            {
                return true;
            }

            return true;
        }

        private async Task UpdateUserPaymentMethod(int userId, PaymentMethod paymentMethod)
        {
            using var connection = await _connectionFactory.GetConnection();

            await connection.ExecuteAsync(@"
UPDATE
    [USER]
SET
    CCHolderFullName = @ccHolderFullName,
    CCNumber = @ccNumber,
    CCExpMonth = @ccExpMonth,
    CCExpYear = @ccExpYear,
    CCVerification = @ccVerification,
    IdCCType = (SELECT IdCCType FROM [CreditCardTypes] WHERE Description = @idCCType),
    PaymentMethod = (SELECT IdPaymentMethod FROM [PaymentMethods] WHERE PaymentMethodName = @paymentMethodName),
    RazonSocial = @razonSocial,
    IdConsumerType = @idConsumerType,
    IdResponsabileBilling = @idResponsabileBilling
WHERE
    IdUser = @userId;",
            new
            {
                @userId = userId,
                @ccHolderFullName = _encryptionService.EncryptAES256(paymentMethod.CCHolderFullName),
                @ccNumber = _encryptionService.EncryptAES256(paymentMethod.CCNumber),
                @ccExpMonth = paymentMethod.CCExpMonth,
                @ccExpYear = paymentMethod.CCExpYear,
                @ccVerification = _encryptionService.EncryptAES256(paymentMethod.CCVerification),
                @idCCType = paymentMethod.CCType,
                @paymentMethodName = paymentMethod.PaymentMethodName,
                @razonSocial = paymentMethod.RazonSocial,
                @idConsumerType = paymentMethod.IdConsumerType,
                @idResponsabileBilling = (int)ResponsabileBillingEnum.QBL
            });
        }

        private async Task SendUserDataToSap(int userId, PaymentMethod paymentMethod)
        {
            using var connection = await _connectionFactory.GetConnection();

            var user = await connection.QueryFirstOrDefaultAsync<User>(@"
SELECT
    U.IdUser,
    U.BillingEmails,
    U.RazonSocial,
    U.BillingFirstName,
    U.BillingLastName,
    U.BillingAddress,
    U.CityName,
    U.IdState,
    S.CountryCode as StateCountryCode,
    U.Address,
    U.ZipCode,
    U.BillingZip,
    U.Email,
    U.PhoneNumber,
    U.IdConsumerType,
    U.CUIT,
    U.IsCancelated,
    U.SapProperties,
    U.BlockedAccountNotPayed,
    V.IsInbound as IsInbound,
    BS.CountryCode as BillingStateCountryCode,
    U.PaymentMethod,
    (SELECT IdUserType FROM [UserTypesPlans] WHERE IdUserTypePlan = @idUserTypePlan) as IdUserType,
    IdResponsabileBilling,
    U.IdBillingState,
    BS.Name as BillingStateName,
    U.BillingCity
FROM
    [User] U
LEFT JOIN
    [State] S ON S.IdState = U.IdState
LEFT JOIN
    [Vendor] V ON V.IdVendor = U.IdVendor
LEFT JOIN
    [State] BS ON BS.IdState = U.IdBillingState
WHERE
    U.IdUser = @userId;",
                new
                {
                    @userId = userId,
                    @idUserTypePlan = paymentMethod.IdSelectedPlan
                });

            SapBusinessPartner sapDto = new SapBusinessPartner()
            {
                Id = user.IdUser,
                IsClientManager = false
            };

            sapDto.BillingEmails = (user.BillingEmails ?? string.Empty).Replace(" ", string.Empty).Split(',');
            sapDto.FirstName = user.RazonSocial ?? user.BillingFirstName ?? "";
            sapDto.LastName = user.RazonSocial == null ? user.BillingLastName ?? "" : "";
            sapDto.BillingAddress = user.BillingAddress ?? "";
            sapDto.CityName = user.CityName ?? "";
            sapDto.StateId = user.IdState;
            sapDto.CountryCode = user.StateCountryCode ?? "";
            sapDto.Address = user.Address ?? "";
            sapDto.ZipCode = user.ZipCode ?? "";
            sapDto.BillingZip = user.BillingZip ?? "";
            sapDto.Email = user.Email;
            sapDto.PhoneNumber = user.PhoneNumber ?? "";
            sapDto.FederalTaxId = user.IdConsumerType == (int)ConsumerTypeEnum.CF ? (paymentMethod.IdentificationNumber ?? user.CUIT) : user.CUIT;
            sapDto.FederalTaxType = user.IdConsumerType == (int)ConsumerTypeEnum.CF ? paymentMethod.IdentificationType : sapDto.FederalTaxType;
            sapDto.IdConsumerType = user.IdConsumerType;
            sapDto.Cancelated = user.IsCancelated;
            sapDto.SapProperties = JsonConvert.DeserializeObject(user.SapProperties);
            sapDto.Blocked = user.BlockedAccountNotPayed;
            sapDto.IsInbound = user.IsInbound;
            sapDto.BillingCountryCode = user.BillingStateCountryCode ?? "";
            sapDto.PaymentMethod = user.PaymentMethod;
            sapDto.PlanType = user.IdUserType;
            sapDto.BillingSystemId = user.IdResponsabileBilling;
            sapDto.BillingStateId = ((sapDto.BillingSystemId == (int)ResponsabileBillingEnum.QBL || sapDto.BillingSystemId == (int)ResponsabileBillingEnum.QuickBookUSA) && sapDto.BillingCountryCode != "US") ? string.Empty
                : (sapDto.BillingCountryCode == "US") ? (SapDictionary.StatesDictionary.TryGetValue(user.IdBillingState, out string stateIdUs) ? stateIdUs : string.Empty)
                : (SapDictionary.StatesDictionary.TryGetValue(user.IdBillingState, out string stateId) ? stateId : "99");
            sapDto.County = user.BillingStateName ?? "";
            sapDto.BillingCity = user.BillingCity ?? "";

            _sapService.SendUserDataToSap(sapDto);
        }
    }
}
