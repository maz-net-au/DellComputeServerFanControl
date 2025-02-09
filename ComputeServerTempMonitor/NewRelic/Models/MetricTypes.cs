using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor.NewRelic.Models
{
    // https://docs.newrelic.com/docs/data-apis/understand-data/metric-data/metric-data-type/
    public enum MetricTypes
    {
        count,
        cumulativeCount,
        distribution,
        gauge,
        summary,
        uniqueCount
    }
}
