using Doppler.BillingUser.Infrastructure;
using System.Threading.Tasks;

namespace Doppler.BillingUser.Model
{
    public class SmsRepository : ISmsRepository
    {
        private readonly IDatabaseConnectionFactory _connectionFactory;

        public SmsRepository(IDatabaseConnectionFactory connectionFactory)
        {
            _connectionFactory = connectionFactory;
        }

        public Task<int> CreateSmsUserPlanAsync(SmsUserPlan smsUserPlan)
        {
            throw new System.NotImplementedException();
        }
    }
}
