using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor.Hardware.Model
{
    internal class ServerStats
    {
        // only do one for now, make them all the same
        public Dictionary<string, uint> ChassisFansRPM { get; set; } = new Dictionary<string, uint>(); // sdr type Fan
        public uint ChassisFanSpeedPct { get; set; }
        public long ChassisFanSpinDownAt { get; set; } // we only care about the chassis fans slowing down. GPU ones can do what they want
        public float CpuUtilisation { get; set; }
        public float MemoryUtilisation { get; set; }
        public Dictionary<uint, CPUStats> cpuStats { get; set; } = new Dictionary<uint, CPUStats>();
        public int InletTemp { get; set; } // sdr type Temperature
        public int ExhaustTemp { get; set; } // sdr type Temperature
        public uint PowerConsumption { get; set; } // sdr type Current
    }
}
