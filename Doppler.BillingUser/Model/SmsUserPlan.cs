using System;

namespace Doppler.BillingUser.Model
{
    public class SmsUserPlan
    {
        public int IdSmsUserPlan { get; set; }
        public int IdUser { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public int AllowNegativeBalance { get; set; }
        public int IdSmsPlan { get; set; }
        public int SendNotification { get; set; }
    }
}
