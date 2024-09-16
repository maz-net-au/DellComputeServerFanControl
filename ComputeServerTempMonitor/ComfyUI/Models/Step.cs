using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor.ComfyUI.Models
{
    public class Step
    {
        public Dictionary<string, object> inputs { get; set; } = new Dictionary<string, object>();
        public string class_type { get; set; }
        public Dictionary<string, string> _meta { get; set; } = new Dictionary<string, string>();
    }
}
