using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor.ComfyUI.Models
{
    [Flags]
    public enum FlowModelTypes
    {
        unknown = 0x00,
        sdxl = 0x01,
        sd3 = 0x02,
        flux = 0x04,
        sd35 = 0x08
    }
}
