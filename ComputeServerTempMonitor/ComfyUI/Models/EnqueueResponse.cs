using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor.ComfyUI.Models
{
    public class EnqueueResponse
    {
        public string prompt_id { get; set; }
        public int number { get; set; }
        public object node_errors { get; set; } // dunno what this contains yet
    }
}
