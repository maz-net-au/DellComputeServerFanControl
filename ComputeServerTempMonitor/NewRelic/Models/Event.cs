using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor.NewRelic.Models
{
    // https://docs.newrelic.com/docs/data-apis/ingest-apis/event-api/introduction-event-api/
    public class Event
    {
        // what am i going to use events for now?
        // is a power outage an event? 
        public string eventType { get; set; }
        public long timestamp { get; set; } = (DateTime.UtcNow.Ticks - 621355968000000000) / TimeSpan.TicksPerMillisecond;
        public Dictionary<string, object> attributes { get; set; }
    }
}
