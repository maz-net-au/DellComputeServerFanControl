﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ComputeServerTempMonitor.ComfyUI.Models;
using ComputeServerTempMonitor.Software.Models;

namespace ComputeServerTempMonitor.Common
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
        public uint DefaultFanSpeed { get; set; } = 40;
        public int GPUAutoPerfThreshold { get; set; } = 10;
        public int GPUAutoPerfTimeout { get; set; } = 10;
        public List<FanTempSpeeds> CPULimits { get; set; } = new List<FanTempSpeeds>()
{
    new FanTempSpeeds(0, 36, 30)
};
        public List<FanTempSpeeds> GPULimits { get; set; } = new List<FanTempSpeeds>()
{
    new FanTempSpeeds(0, 36, 30)
};
        public Dictionary<string, SoftwareRef> Software = new Dictionary<string, SoftwareRef>();
        public ComfyUIConfig ComfyUI { get; set; } = new ComfyUIConfig();
        public OobaboogaConfig Oobabooga { get; set; } = new OobaboogaConfig();
        public mIoTSettings mIoT { get; set; } = new mIoTSettings();
        public NewRelicConfig NewRelic { get; set; } = new NewRelicConfig();
    }
    public class FanTempSpeeds
    {
        public FanTempSpeeds(int temp, uint speed, uint cardSpeed)
        {
            Temp = temp;
            MinSpeed = speed;
            MinCardSpeed = cardSpeed;
        }
        public int Temp { get; set; }
        public uint MinCardSpeed { get; set; }
        public uint MinSpeed { get; set; }
    }

    public class ComfyUIConfig
    {
        public Dictionary<string, ComfyUIFlow> Flows { get; set; } = new Dictionary<string, ComfyUIFlow>();
        public Dictionary<string, List<string>> Options { get; set; } = new Dictionary<string, List<string>>();
        public string URL { get; set; }
        public ComfyPathsConfig Paths { get; set; } = new ComfyPathsConfig();
        public ComfyUISettings Settings { get; set; }
    }
    public class NewRelicConfig
    {
        public string LicenseKey { get; set; } = "";
        public int PushInterval { get; set; } = 15;
        public uint MaxPayloadSize { get; set; } = 1000000;
        public bool ForwardLogs { get; set; } = false;
        public bool EnableSending { get; set; } = true;
        public NewRelicURLsConfig URLs { get; set; } = new NewRelicURLsConfig();
    }
    public class NewRelicURLsConfig
    {
        public string Events { get; set; }
        public string Metrics { get; set; }
        public string Logs { get; set; }
    }

    public class ComfyPathsConfig
    {
        public string Checkpoints { get; set; }
        public string Unets { get; set; }
        public string LoRAs { get; set; }
        public string Prompts { get; set; }
        public string Temp { get; set; }
        public string Inputs { get; set; }
        public string Outputs { get; set; }
    }

    public class ComfyUISettings
    {
        public int CompletionPollingRate { get; set; }
        public int MaximumFileSize { get; set; }
        public int MaximumControlsDimension { get; set; }
    }

    public class mIoTSettings
    {
        public string URL { get; set; } = "";
        public List<string> CameraNames { get; set; } = new List<string>();

    }
    public class ComfyUIField
    {
        public ComfyUIField() { }

        public ComfyUIField(string nodeTitle, string field, dynamic value, string obj = "")
        {
            NodeTitle = nodeTitle;
            Field = field;
            Value = value;
            Object = obj;
        }
        public string NodeTitle { get; set; } = "";
        public string Field { get; set; } = "";
        public string Object { get; set; } = "";
        public string Type { get; set; } = "";
        public bool Required { get; set; } = false;
        public string Filter { get; set; } = "";
        public dynamic Value { get; set; }
    }
}