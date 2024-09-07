using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor.ComfyUI
{
    public class QueueInfo
    {
        public ExecutionInfo exec_info { get; set; }
    }

    public class ExecutionInfo
    {
        public int queue_remaining { get; set; }
    }
}
