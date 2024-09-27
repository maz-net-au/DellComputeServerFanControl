using ComputeServerTempMonitor.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor.ComfyUI.Models
{
    public class ComfyUIFlow
    {
        public bool Visible { get; set; }
        public FlowModelTypes Type { get; set; }
        public Dictionary<string, ComfyUIField> Fields { get; set; }
    }
}
