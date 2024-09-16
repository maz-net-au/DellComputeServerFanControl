using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor.ComfyUI.Models
{
    public class HistoryResponse
    {
        public List<object> prompt { get; set; }
        public Dictionary<string, Dictionary<string, List<Image>>> outputs { get; set; }
        public Status status { get; set; }
    }
}
