using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor.Hardware.Model
{
    public enum GPUPerformanceState
    {
        Auto = 0,
        High = 1,
        Low = 2
    }

    public class GPUStats
    {
        public string Name { get; set; } = "";
        public GPUPerformanceState State { get; set; } = GPUPerformanceState.Auto;
        public uint LastUtilisation { get; set; } = 0;
        public long ResetAt { get; set; } = 0;
        public int LastTemp { get; set; } = 0;
        public uint FanSpeed { get; set; } = 0;
        public uint RequestedFanSpeed { get; set; } = 0;
    }
}
