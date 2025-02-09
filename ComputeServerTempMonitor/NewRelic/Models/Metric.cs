using Newtonsoft.Json.Converters;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor.NewRelic.Models
{
    public class Metric
    {
        public string name { get; set; }
        [Newtonsoft.Json.JsonConverter(typeof(StringEnumConverter))]
        public MetricTypes type { get; set; } = MetricTypes.gauge;
        public double value { get; set; }
        public long timestamp { get; set; } = (DateTime.UtcNow.Ticks - 621355968000000000) / TimeSpan.TicksPerMillisecond;
        //public Dictionary<string, object> attributes { get; set; } = new Dictionary<string, object>();
    }
}
