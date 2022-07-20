using Doppler.BillingUser.Model;
using System.Threading.Tasks;

namespace Doppler.BillingUser.Infrastructure
{
    public interface ISmsRepository
    {
        Task<int> CreateSmsUserPlanAsync(SmsUserPlan smsUserPlan);
    }
}
