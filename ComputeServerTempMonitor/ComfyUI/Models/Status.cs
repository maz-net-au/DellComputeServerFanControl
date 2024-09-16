using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor.ComfyUI.Models
{
    public class Status
    {
        public string status_str { get; set; }
        public bool completed { get; set; }
        public List<List<object>> messages { get; set; }
    }
}
