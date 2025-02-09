using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor.NewRelic.Models
{
    public class LogMessage
    {
        public string level { get; set; }
        public long timestamp { get; set; } = (DateTime.UtcNow.Ticks - 621355968000000000) / TimeSpan.TicksPerMillisecond;
        public string message { get; set; }
        public string logtype { get; set; }
        //public Dictionary<string, object> attributes { get; set; } // severity?
    }
}
