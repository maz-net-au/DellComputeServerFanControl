using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor.Hardware.Model
{
    internal class CPUStats
    {
        public string Name { get; set; } = "";
        public int LastTemp { get; set; } = 0; // sdr type Temperature
    }
}
