using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor.ComfyUI.Models
{
    public class QueuedRequest
    {
        public ulong RequestId { get; set; }
        public string PromptId { get; set; }
    }
}
