using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor
{
    public class Config
    {
        public string SMIPath { get; set; } = "";
        public string IPMIPath { get; set; } = "";
        public string IPMIInterface { get; set; } = "wmi";
        public string IPMILogin { get; set; } = "";
        public int GPUCheckingInterval { get; set; } = 5;
        public int CPUCheckingInterval { get; set; } = 5;
        public int FanSpinDownDelay { get; set; } = 3600;
        public int DefaultFanSpeed { get; set; } = 25;
        public List<FanTempSpeeds> CPULimits { get; set; } = new List<FanTempSpeeds>()
        {
            new FanTempSpeeds(0, 32)
        };

        public List<FanTempSpeeds> GPULimits { get; set; } = new List<FanTempSpeeds>()
        {
            new FanTempSpeeds(0, 32)
        };
    }
    public class FanTempSpeeds
    {
        public FanTempSpeeds(int temp, int speed) 
        {
            Temp = temp;
            MinSpeed = speed;
        }
        public int Temp { get; set; }
        public int MinSpeed { get; set; }
    }
}
