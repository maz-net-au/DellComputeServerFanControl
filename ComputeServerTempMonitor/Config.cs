using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor
{
    public class Config
    {
        public string DiscordBotToken { get; set; } = "";
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
        public Dictionary<string, SoftwareRef> Software = new Dictionary<string, SoftwareRef>();
        public ComfyUIConfig ComfyUI { get; set; } = new ComfyUIConfig();
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

    public class ComfyUIConfig
    {
        public Dictionary<string, Dictionary<string, ComfyUIField>> Flows { get; set; } = new Dictionary<string, Dictionary<string, ComfyUIField>>();
        public Dictionary<string, List<string>> Options { get; set; } = new Dictionary<string, List<string>>();
        public string URL { get; set; } = "";
        public string ModelDirectory { get; set; } = ""; // should populate the list of models from here instead of hard-coding them
    }

    public class ComfyUIField
    {
        public string NodeTitle { get; set; } = "";
        public string Field { get; set; } = "";
        public string Type { get; set; } = "";
    }
}
