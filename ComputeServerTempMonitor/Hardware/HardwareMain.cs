﻿using ComputeServerTempMonitor.Common;
using ComputeServerTempMonitor.Hardware.Model;
using NvAPIWrapper.Native.GPU.Structures;
using NvAPIWrapper.Native;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static NvAPIWrapper.Native.GPU.Structures.PrivateFanCoolersControlV1;
using System.Threading;
using System.Diagnostics;
using static System.Net.WebRequestMethods;
using System.Reflection;
using ComputeServerTempMonitor.NewRelic;
using ComputeServerTempMonitor.NewRelic.Models;
using YamlDotNet.Core;
using Discord;
using Newtonsoft.Json;

namespace ComputeServerTempMonitor.Hardware
{
    // everything that reads hardware state (temps, perf, utilisation etc) and then
    // sets things like fans
    public static class HardwareMain
    {

        static Dictionary<uint, GPUStats> gpuPerf = new Dictionary<uint, GPUStats>();
        static Dictionary<string, string> fanDetails = new Dictionary<string, string>();
        static ServerStats serverStats = new ServerStats();

        static CancellationToken cancellationToken;

        static GPUPerformanceState GPUPerfState = GPUPerformanceState.Auto;

        static PerformanceCounter cpuCounter;

        static CancellationTokenSource shutdownCanceller = new CancellationTokenSource();
        static Task Shutdown;

        static bool CPUStarted = false;
        static bool GPUStarted = false;
        static bool PerfStarted = false;
        static bool FanStarted = false;

        public static void Init(CancellationToken ct)
        {
            cancellationToken = ct;
            cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            // first call to each is 0
            cpuCounter.NextValue();

            serverStats.ChassisFanSpinDownAt = DateTime.Now.AddSeconds(SharedContext.Instance.GetConfig().FanSpinDownDelay).Ticks;


            if (SharedContext.Instance.GetConfig().IPMIPath != "")
            {
                if (!System.Diagnostics.Debugger.IsAttached)
                {
                    // on startup, call IPMI commands to disable PCIe card response
                    List<string> result = SharedContext.ExecuteCLI(SharedContext.Instance.GetConfig().IPMIPath, $"-I {SharedContext.Instance.GetConfig().IPMIInterface}{SharedContext.Instance.GetConfig().IPMILogin} raw 0x30 0xce 0x00 0x16 0x05 0x00 0x00 0x00 0x05 0x00 0x00 0x00 0x00");
                    // set fan manual mode
                    result = SharedContext.ExecuteCLI(SharedContext.Instance.GetConfig().IPMIPath, $"-I {SharedContext.Instance.GetConfig().IPMIInterface}{SharedContext.Instance.GetConfig().IPMILogin} raw 0x30 0x30 0x01 0x00");
                }
                // set default rate
                SetChassisFanSpeed(SharedContext.Instance.GetConfig().DefaultFanSpeed);
                serverStats.ChassisFanSpeedPct = SharedContext.Instance.GetConfig().DefaultFanSpeed;
                SharedContext.Instance.Log(LogLevel.INFO, "Main", "Chassis fan control set to manual");
            }

            // in a task
            Task tGpu = new Task(async () =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        // because there seems to be no way to link nvidia-smi output and physicallTCC cards from nvapi,
                        //  I have to do everything via nvapi because I need that one for setting pstates.
                        PhysicalGPUHandle[] handles = GPUApi.EnumTCCPhysicalGPUs();
                        for (uint i = 0; i < handles.Length; i++)
                        {
                            // do we store this with a #id? its an in-memory setup and you don't hot-swap gpus... right?
                            if (!gpuPerf.ContainsKey(i))
                            {
                                gpuPerf[i] = new GPUStats();
                                gpuPerf[i].Name = GPUApi.GetFullName(handles[i]);
                            }
                            var t = GPUApi.GetThermalSettings(handles[i]);
                            int currentMaxTemp = 0;
                            foreach (var sensor in t.Sensors)
                            {
                                // all of my  GPU's only have one temp sensor. but if there's multiple, take the max
                                //Console.WriteLine($"GPU_{i} - Sensor {sensor.Controller.ToString()} - {sensor.CurrentTemperature}");
                                currentMaxTemp = Math.Max(currentMaxTemp, sensor.CurrentTemperature);
                            }
                            gpuPerf[i].LastTemp = currentMaxTemp;
                            var x = GPUApi.GetUsages(handles[i]);
                            gpuPerf[i].LastUtilisation = x.Domains[NvAPIWrapper.Native.GPU.UtilizationDomain.GPU].Percentage;
                            try
                            {
                                uint coolerID = GPUApi.GetClientFanCoolersControl(handles[i]).FanCoolersControlEntries.FirstOrDefault().CoolerId;
                                //GPUApi.SetClientFanCoolersControl(ph, new PrivateFanCoolersControlV1(new FanCoolersControlEntry[] { new PrivateFanCoolersControlV1.FanCoolersControlEntry(coolerID, NvAPIWrapper.Native.GPU.FanCoolersControlMode.Manual, 61) }));
                                //Console.WriteLine("Set card fan speed");
                                gpuPerf[i].FanSpeed = GPUApi.GetClientFanCoolersControl(handles[i]).FanCoolersControlEntries.FirstOrDefault().Level;
                            }
                            catch (Exception ex) { }
                            string logName = $"gpu.{gpuPerf[i].Name.Replace(" ", "").Replace("_", "")}.{i}.".ToLower();
                            NewRelicMain.Log(new Metric(){ name = logName + "temp", value = gpuPerf[i].LastTemp });
                            NewRelicMain.Log(new Metric() { name = logName + "fanspeed", value = gpuPerf[i].FanSpeed });
                            NewRelicMain.Log(new Metric() { name = logName + "utilisation", value = gpuPerf[i].LastUtilisation });
                        }
                        GPUStarted = true;
                        Thread.Sleep(SharedContext.Instance.GetConfig().GPUCheckingInterval * 1000);
                    }
                    catch (Exception ex)
                    {
                        SharedContext.Instance.Log(LogLevel.ERR, "Main", "Error reading GPU temps: " + ex.Message);
                    }
                }
            }, cancellationToken);
            tGpu.Start();

            // periodically check the power consumption, memory clocks and memory utilisation
            Task tGpu2 = new Task(async () =>
            {
                if (SharedContext.Instance.GetConfig().SMIPath == "")
                {
                    SharedContext.Instance.Log(LogLevel.ERR, "HardwareMain", "SMIPath not set.");
                    return;
                }
                if (!System.IO.File.Exists(SharedContext.Instance.GetConfig().SMIPath))
                {
                    SharedContext.Instance.Log(LogLevel.ERR, "HardwareMain", "SMIPath not found.");
                    return;
                }
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        if (GPUStarted)
                        {
                            // get other details from smi
                            List<string> smiOut = SharedContext.ExecuteCLI(SharedContext.Instance.GetConfig().SMIPath, $"dmon -s pcm -c 1");
                            /*
    C:\Users\Administrator>nvidia-smi dmon -s pcm -c 1
    # gpu   pwr gtemp mtemp  mclk  pclk    fb  bar1
    # Idx     W     C     C   MHz   MHz    MB    MB
        0     14     34      -    405    645  41205      5
        1      9     37      -    405    645  40110      7
                            */
                            foreach (string line in smiOut)
                            {
                                string l = line.Trim();
                                if (l == "" || l.StartsWith("#"))
                                    continue;
                                string[] gpuD = l.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                                // which is the temp?
                                if (gpuD.Length >= 8)
                                {
                                    //Console.WriteLine(string.Join(",", gpuD));
                                    uint i = uint.Parse(gpuD[0]);
                                    // gpuD[1] is power in Watts
                                    // gpuD[4] is mem clock
                                    // gpuD[5] is gpu clock
                                    // gpuD[6] is used vram in MB
                                    gpuPerf[i].PowerConsumption = uint.Parse(gpuD[1]);
                                    gpuPerf[i].MemoryClock = uint.Parse(gpuD[4]);
                                    gpuPerf[i].ProcessorClock = uint.Parse(gpuD[5]);
                                    gpuPerf[i].UsedVRAM = uint.Parse(gpuD[6]);
                                    string logName = $"gpu.{gpuPerf[i].Name.Replace(" ", "").Replace("_", "")}.{i}.".ToLower();
                                    NewRelicMain.Log(new Metric() { name = logName + "power", value = gpuPerf[i].PowerConsumption });
                                    NewRelicMain.Log(new Metric() { name = logName + "mclk", value = gpuPerf[i].MemoryClock });
                                    NewRelicMain.Log(new Metric() { name = logName + "pclk", value = gpuPerf[i].ProcessorClock });
                                    NewRelicMain.Log(new Metric() { name = logName + "usedvram", value = gpuPerf[i].UsedVRAM });
                                }
                            }
                        }
                        Thread.Sleep(SharedContext.Instance.GetConfig().GPUCheckingInterval * 1000);
                    }
                    catch (Exception ex)
                    {
                        SharedContext.Instance.Log(LogLevel.ERR, "Main", "Error reading SMI data: " + ex.Message);
                    }
                }
            }, cancellationToken);
            tGpu2.Start();

            // in a task
            Task tCpu = new Task(async () =>
            {
                if (SharedContext.Instance.GetConfig().IPMIPath == "")
                {
                    SharedContext.Instance.Log(LogLevel.ERR, "HardwareMain", "IPMIPath not set.");
                }
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        // get temperatures from IPMI
                        List<string> ipmiOut = SharedContext.ExecuteCLI(SharedContext.Instance.GetConfig().IPMIPath, $"-I {SharedContext.Instance.GetConfig().IPMIInterface}{SharedContext.Instance.GetConfig().IPMILogin} sdr type Temperature");
                        // call IPMI commands to get cpu temps
                        uint cpuNum = 0;
                        foreach (string line in ipmiOut)
                        {
                            string l = line.Trim();
                            if (l == "" || l.StartsWith("#"))
                                continue;
                            string[] cpuD = l.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                            // which is the temp?
                            if (cpuD.Length >= 3)
                            {
                                switch (cpuD[0])
                                {
                                    case "Temp": // cpu temp
                                        {
                                            if (!serverStats.cpuStats.ContainsKey(cpuNum))
                                            {
                                                serverStats.cpuStats[cpuNum] = new CPUStats();
                                                serverStats.cpuStats[cpuNum].Name = "CPU_" + cpuNum;
                                            }
                                            int temp = int.Parse(cpuD[4].Replace(" degrees C", ""));
                                            serverStats.cpuStats[cpuNum].LastTemp = temp;
                                            NewRelicMain.Log(new Metric() { name = $"cpu.{cpuNum}.temp", value = serverStats.cpuStats[cpuNum].LastTemp });
                                            cpuNum++;
                                        }
                                        break;
                                    case "Inlet Temp":
                                        {
                                            int temp = int.Parse(cpuD[4].Replace(" degrees C", ""));
                                            serverStats.InletTemp = temp;
                                            NewRelicMain.Log(new Metric() { name = $"chassis.inlet.temp", value = serverStats.InletTemp });
                                        }
                                        break;
                                    case "Exhaust Temp":
                                        {
                                            int temp = int.Parse(cpuD[4].Replace(" degrees C", ""));
                                            serverStats.ExhaustTemp = temp;
                                            NewRelicMain.Log(new Metric() { name = $"chassis.exhaust.temp", value = serverStats.ExhaustTemp });
                                        }
                                        break;
                                }
                            }
                        }
                        // get power usage from IPMI
                        List<string> ipmiPowerOut = SharedContext.ExecuteCLI(SharedContext.Instance.GetConfig().IPMIPath, $"-I {SharedContext.Instance.GetConfig().IPMIInterface}{SharedContext.Instance.GetConfig().IPMILogin} sdr type Current");
                        // call IPMI commands to get cpu temps
                        foreach (string line in ipmiPowerOut)
                        {
                            string l = line.Trim();
                            if (l == "" || l.StartsWith("#"))
                                continue;
                            string[] pwrD = l.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                            // which is the temp?
                            if (pwrD.Length >= 3)
                            {
                                switch (pwrD[0])
                                {
                                    case "Pwr Consumption": // cpu temp
                                        {
                                            uint watts = uint.Parse(pwrD[4].Replace(" Watts", ""));
                                            serverStats.PowerConsumption = watts;
                                            NewRelicMain.Log(new Metric() { name = $"chassis.power", value = serverStats.PowerConsumption });
                                        }
                                        break;
                                }
                            }
                        }

                        // get fan speeds from IPMI
                        List<string> ipmiFansOut = SharedContext.ExecuteCLI(SharedContext.Instance.GetConfig().IPMIPath, $"-I {SharedContext.Instance.GetConfig().IPMIInterface}{SharedContext.Instance.GetConfig().IPMILogin} sdr type Fan");
                        // call IPMI commands to get cpu temps
                        foreach (string line in ipmiFansOut)
                        {
                            string l = line.Trim();
                            if (l == "" || l.StartsWith("#"))
                                continue;
                            string[] fanD = l.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                            // which is the temp?
                            if (fanD.Length >= 3)
                            {
                                if (fanD[0] != "Fan Redundancy")
                                {
                                    uint rpm = uint.Parse(fanD[4].Replace(" RPM", ""));
                                    serverStats.ChassisFansRPM[fanD[0]] = rpm;
                                    NewRelicMain.Log(new Metric() { name = $"chassis.fan.{fanD[0].Substring(fanD[0].Length-1)}.rpm", value = serverStats.ChassisFansRPM[fanD[0]] });
                                }
                            }
                        }

                        serverStats.CpuUtilisation = cpuCounter.NextValue();
                        NewRelicMain.Log(new Metric() { name = $"cpu.utilisation", value = serverStats.CpuUtilisation });
                        GCMemoryInfo memInfo = GC.GetGCMemoryInfo();
                        serverStats.MemoryUtilisation = (memInfo.MemoryLoadBytes * 100.0f) / memInfo.TotalAvailableMemoryBytes; // percent
                        NewRelicMain.Log(new Metric() { name = $"memory.utilisation", value = serverStats.MemoryUtilisation });
                        NewRelicMain.Log(new Metric() { name = $"memory.usedbytes", value = memInfo.MemoryLoadBytes * 100.0f });
                        try
                        {
                            BatteryInformation bi = BatteryInfo.GetBatteryInformation();
                            if (bi != null)
                            {
                                // if we've gone offline since the last check, Send a message
                                if (serverStats.PowerOnline && ((int)bi.PowerState & 0x00000001) == 0)
                                {
                                    SharedContext.Instance.Log(LogLevel.WARN, "Hardware", $"Power has been lost. Starting shutdown timer for {SharedContext.Instance.GetConfig().PowerLossShutdownTimer} sec");
                                    serverStats.PowerOnline = false;
                                    Shutdown = new Task(async () =>
                                    {
                                        try
                                        {
                                            await Task.Delay(SharedContext.Instance.GetConfig().PowerLossShutdownTimer * 1000);
                                            BatteryInformation bi = BatteryInfo.GetBatteryInformation();
                                            if (bi != null)
                                            {
                                                // if we've gone offline since the last check, Send a message
                                                if (((int)bi.PowerState & 0x00000001) == 0)
                                                {
                                                    SharedContext.Instance.Log(LogLevel.ERR, "Power", "Shutting down! Shut down after power loss timer has elapsed and power is still off.");
                                                    // shut down
                                                    NewRelicMain.Flush();
                                                    // do we want to terminate any software that is running first?
                                                    ProcessStartInfo psi = new ProcessStartInfo("shutdown", "/s /t 0 /f");
                                                    psi.CreateNoWindow = true;
                                                    psi.UseShellExecute = false;
                                                    Process.Start(psi);
                                                    return;
                                                }
                                                SharedContext.Instance.Log(LogLevel.WARN, "Power", "Power restored. Shutdown aborted.");
                                            }
                                        }
                                        catch (Exception ex)
                                        {

                                        }
                                    }, shutdownCanceller.Token);
                                    Shutdown.Start();
                                }
                                else if (!serverStats.PowerOnline && ((int)bi.PowerState & 0x00000001) > 0)
                                {
                                    SharedContext.Instance.Log(LogLevel.WARN, "Hardware", $"Power restored.");
                                    serverStats.PowerOnline = true;
                                    shutdownCanceller.Cancel();
                                    shutdownCanceller.Dispose();
                                    shutdownCanceller = new CancellationTokenSource(); // get ready to do it all again
                                }
                                //SharedContext.Instance.Log(LogLevel.INFO, "Hardware", JsonConvert.SerializeObject(bi));
                                NewRelicMain.Log(new Metric() { name = $"ups.battery.rate", value = bi.Rate });
                                NewRelicMain.Log(new Metric() { name = $"ups.powerstate", value = (int)bi.PowerState });
                                NewRelicMain.Log(new Metric() { name = $"ups.online", value = (int)bi.PowerState & 0x00000001 });
                                NewRelicMain.Log(new Metric() { name = $"ups.battery.currentcapacity", value = bi.CurrentCapacity });
                                NewRelicMain.Log(new Metric() { name = $"ups.battery.designedcapacity", value = bi.DesignedCapacity });
                                NewRelicMain.Log(new Metric() { name = $"ups.battery.cyclecount", value = bi.CycleCount });
                                NewRelicMain.Log(new Metric() { name = $"ups.battery.fullchargecapacity", value = bi.FullChargeCapacity });
                                NewRelicMain.Log(new Metric() { name = $"ups.battery.capacity", value = bi.CurrentCapacity / bi.FullChargeCapacity });
                                NewRelicMain.Log(new Metric() { name = $"ups.battery.voltage", value = bi.Voltage / 1000.0 });
                            }
                            else
                            {
                                //SharedContext.Instance.Log(LogLevel.INFO, "Hardware", "Battery info not available");
                            }
                        }
                        catch (Exception ex)
                        {
                            SharedContext.Instance.Log(LogLevel.INFO, "Hardware", "Battery info crashed: " + ex.ToString());
                        }
                        // poll
                        CPUStarted = true;
                        Thread.Sleep(SharedContext.Instance.GetConfig().CPUCheckingInterval * 1000);
                    }
                    catch (Exception ex)
                    {
                        SharedContext.Instance.Log(LogLevel.ERR, "Main", "Error reading IPMI data: " + ex.ToString());
                    }
                }
            }, cancellationToken);
            tCpu.Start();

            Task tPerf = new Task(async () =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        if (!serverStats.PowerOnline)
                        {
                            foreach (uint id in gpuPerf.Keys)
                            {
                                if (gpuPerf[id].State != GPUPerformanceState.Low)
                                {
                                    if (GPUSleep(id))
                                    {
                                        gpuPerf[id].State = GPUPerformanceState.Low;
                                        SharedContext.Instance.Log(LogLevel.WARN, "Power", $"GPU {id} forced into an idle state while power offline.");
                                    }
                                }
                            }
                        }
                        else if (GPUPerfState == GPUPerformanceState.Auto)
                        {
                            foreach (uint id in gpuPerf.Keys)
                            {
                                if (gpuPerf[id].LastUtilisation >= SharedContext.Instance.GetConfig().GPUAutoPerfThreshold)
                                {
                                    // compute go brrrrrr
                                    if (gpuPerf[id].State != GPUPerformanceState.High)
                                    {
                                        if (GPUWake(id))
                                        {
                                            gpuPerf[id].State = GPUPerformanceState.High;
                                            SharedContext.Instance.Log(LogLevel.INFO, "PerformanceTask", $"GPU {id} entered a high performance state");
                                        }
                                    }
                                    long newTimeout = DateTime.Now.AddSeconds(SharedContext.Instance.GetConfig().GPUAutoPerfTimeout).Ticks;
                                    if (gpuPerf[id].ResetAt < newTimeout)
                                    {
                                        gpuPerf[id].ResetAt = newTimeout;
                                    }
                                }
                                else
                                {
                                    if (gpuPerf[id].ResetAt < DateTime.Now.Ticks)
                                    {
                                        // compute zzzzz
                                        if (gpuPerf[id].State != GPUPerformanceState.Low)
                                        {
                                            if (GPUSleep(id))
                                            {
                                                gpuPerf[id].State = GPUPerformanceState.Low;
                                                SharedContext.Instance.Log(LogLevel.INFO, "PerformanceTask", $"GPU {id} entered an idle state");
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        PerfStarted = true;
                        Thread.Sleep(1000);
                    }
                    catch (Exception ex)
                    {
                        SharedContext.Instance.Log(LogLevel.ERR, "Main", "Error reading GPU perf: " + ex.Message);
                    }
                }
            }, cancellationToken);
            tPerf.Start();

            // fan control thread
            Task tFan = new Task(async () =>
            {
                Thread.Sleep(10000); // fan control doesn't kick in for 10s
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        uint newSpeed = 0;
                        // watch for cpu cores + gpu cores.
                        int maxCPU = GetCurrentMaxCPUTemp();
                        foreach (FanTempSpeeds fts in SharedContext.Instance.GetConfig().CPULimits)
                        {
                            if (maxCPU >= fts.Temp)
                                newSpeed = Math.Max(newSpeed, fts.MinSpeed);
                        }
                        int maxGPU = GetCurrentMaxGPUTemp();
                        // loop through each GPU, and control them individually
                        foreach (KeyValuePair<uint, GPUStats> kvp in gpuPerf)
                        {
                            uint newGPUSpeed = 0;
                            foreach (FanTempSpeeds fts in SharedContext.Instance.GetConfig().GPULimits)
                            {
                                if (kvp.Value.LastTemp >= fts.Temp)
                                {
                                    // find the single new value to set this card's fan speed to
                                    newGPUSpeed = Math.Max(newGPUSpeed, fts.MinCardSpeed);
                                    newSpeed = Math.Max(newSpeed, fts.MinSpeed);
                                }
                            }
                            // only allow the speed to drop once the card has idled. otherwise they go up and down heaps
                            if (newGPUSpeed > kvp.Value.RequestedFanSpeed)
                            {
                                SharedContext.Instance.Log(LogLevel.INFO, "FanTask", SetGPUFanSpeed(kvp.Key, newGPUSpeed));
                            }
                            else if (newGPUSpeed < kvp.Value.RequestedFanSpeed && kvp.Value.State == GPUPerformanceState.Low)
                            {
                                SharedContext.Instance.Log(LogLevel.INFO, "FanTask", SetGPUFanSpeed(kvp.Key, newGPUSpeed));
                            }
                        }
                        
                        fanDetails["Fan_%"] = serverStats.ChassisFanSpeedPct.ToString();
                        fanDetails["To__%"] = newSpeed.ToString();
                        if (newSpeed >= serverStats.ChassisFanSpeedPct)
                        {
                            // once fans ramp, set a timer to delay the spin-down response until idle for a long time
                            // dont reduce / reset any extended timeout
                            if (serverStats.ChassisFanSpinDownAt < DateTime.Now.AddSeconds(SharedContext.Instance.GetConfig().FanSpinDownDelay).Ticks)
                                serverStats.ChassisFanSpinDownAt = DateTime.Now.AddSeconds(SharedContext.Instance.GetConfig().FanSpinDownDelay).Ticks;
                        }
                        if (newSpeed > serverStats.ChassisFanSpeedPct || DateTime.Now.Ticks > serverStats.ChassisFanSpinDownAt)
                        {
                            // call IPMI commands to set fan speed
                            //result = exec(config.IPMIPath, $"-I {config.IPMIInterface}{config.IPMILogin} raw 0x30 0x30 0x02 0xff 0x{newSpeed.ToString("x2")}");
                            SetChassisFanSpeed(newSpeed);
                            serverStats.ChassisFanSpeedPct = newSpeed;
                            SharedContext.Instance.Log(LogLevel.INFO, "FanTask", $"Fan speed is now {newSpeed}% until {new DateTime(serverStats.ChassisFanSpinDownAt).ToString()}");
                            //printState(true);
                        }
                        NewRelicMain.Log(new Metric() { name = $"chassis.fans.percent", value = serverStats.ChassisFanSpeedPct });
                        fanDetails["Until"] = new DateTime(serverStats.ChassisFanSpinDownAt).ToString();
                        FanStarted = true;
                        Thread.Sleep(3000);
                    }
                    catch (Exception ex)
                    {
                        SharedContext.Instance.Log(LogLevel.ERR, "FanTask", "Error processing fan speeds: " + ex.ToString());
                    }
                }
            }, cancellationToken);
            tFan.Start();
        }

        // instead of periodically reading the state, we should raise a metric / log / event when something happens
        // let the newrelic module sort out the batching and sending of that
        //public static Dictionary<string, object>? GetMonitoring()
        //{
        //    if (CPUStarted && GPUStarted && PerfStarted && FanStarted)
        //    {
        //        Dictionary<string, object> stats = new Dictionary<string, object>();
        //        foreach (KeyValuePair<uint, CPUStats> cpu in serverStats.cpuStats)
        //        {
        //            stats[cpu.Value.Name.Replace("_", "") + "Temp"] = cpu.Value.LastTemp;
        //            stats[cpu.Value.Name.Replace("_", "") + "Util"] = serverStats.CpuUtilisation;
        //        }
        //        stats["InletTemp"] = serverStats.InletTemp;
        //        stats["ExhaustTemp"] = serverStats.ExhaustTemp;
        //        stats["MemoryUtil"] = serverStats.MemoryUtilisation;
        //        stats["PowerConsumption"] = serverStats.PowerConsumption;
        //        int i = 0;
        //        foreach (KeyValuePair<uint, GPUStats> gpu in gpuPerf)
        //        {
        //            stats[$"{gpu.Value.Name.Replace(" ", "")}{i}Temp"] = gpu.Value.LastTemp;
        //            stats[$"{gpu.Value.Name.Replace(" ", "")}{i}FanSpeed"] = gpu.Value.FanSpeed;
        //            stats[$"{gpu.Value.Name.Replace(" ", "")}{i}Util"] = gpu.Value.LastUtilisation;
        //            stats[$"{gpu.Value.Name.Replace(" ", "")}{i}PowerState"] = (GPUPerfState == GPUPerformanceState.Auto ? Enum.GetName(gpu.Value.State) : Enum.GetName(GPUPerfState));
        //            i++;
        //        }
        //        stats["ChassisFansSpeed"] = serverStats.ChassisFanSpeedPct;
        //        foreach (KeyValuePair<string, uint> fan in serverStats.ChassisFansRPM)
        //        {
        //            stats[fan.Key + "RPM"] = fan.Value;
        //        }
        //        stats["ChassisFanSpeedTTL"] = serverStats.ChassisFanSpinDownAt / TimeSpan.TicksPerMillisecond;
        //        return stats;
        //    }
        //    return null;
        //}

        public static string GetCLIStatus(bool showHeaders)
        {
            string headers = "";
            string values = "";

            foreach (CPUStats s in serverStats.cpuStats.Values)
            {
                headers += $"{s.Name}\t";
                values += $"{s.LastTemp}\t";
            }
            foreach (uint s in gpuPerf.Keys)
            {
                headers += $"GPU_{s}\t";
                values += $"{gpuPerf[s].LastTemp}\t";
            }
            foreach (string s in fanDetails.Keys)
            {
                headers += $"{s}\t";
                values += $"{fanDetails[s]}\t";
            }
            return $"{(showHeaders ? headers + "\n" : "")}{values}";
        }

        public static int GetCurrentMaxGPUTemp()
        {
            int maxTemp = -999;
            foreach (KeyValuePair<uint, GPUStats> kvp in gpuPerf)
            {
                maxTemp = Math.Max(maxTemp, kvp.Value.LastTemp);
            }
            return maxTemp;
        }

        public static int GetCurrentMaxCPUTemp()
        {
            int maxTemp = -999;
            foreach (KeyValuePair<uint, CPUStats> kvp in serverStats.cpuStats)
            {
                maxTemp = Math.Max(maxTemp, kvp.Value.LastTemp);
            }
            return maxTemp;
        }

        public static uint GetChassisFanSpeed()
        {
            return serverStats.ChassisFanSpeedPct;
        }

        public static long GetChassisFanSpindown()
        {
            return serverStats.ChassisFanSpinDownAt;
        }

        public static string GetStatus()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"Server: ");
            sb.AppendLine($"\t{"Name".PadRight(8, ' ')}Temp\t   Use");
            foreach (KeyValuePair<uint, CPUStats> cpu in serverStats.cpuStats)
            {
                sb.AppendLine($"\t{cpu.Value.Name.PadRight(8, ' ')}{cpu.Value.LastTemp.ToString().PadLeft(4, ' ')}\t{serverStats.CpuUtilisation.ToString("0.0").PadLeft(5, ' ')}%");
            }
            sb.AppendLine($"\t{"Memory".PadRight(8, ' ')}    \t{serverStats.MemoryUtilisation.ToString("0.0").PadLeft(5, ' ')}%");
            sb.AppendLine($"\t{"Power".PadRight(8, ' ')}    \t{serverStats.PowerConsumption.ToString().PadLeft(4, ' ')} W");
            sb.AppendLine($"\t{"Inlet".PadRight(8, ' ')}{serverStats.InletTemp.ToString().PadLeft(4, ' ')}");
            sb.AppendLine($"\t{"Exhaust".PadRight(8, ' ')}{serverStats.ExhaustTemp.ToString().PadLeft(4, ' ')}");
            sb.AppendLine($"\nGPU Status: ");
            sb.AppendLine($"\t{"Name".PadRight(16, ' ')} Temp\t Util\tFan%\tPerf");
            foreach (KeyValuePair<uint, GPUStats> gpu in gpuPerf)
            {
                sb.AppendLine($"\t{gpu.Value.Name.PadRight(16, ' ')} {gpu.Value.LastTemp.ToString().PadLeft(4)}\t{gpu.Value.LastUtilisation.ToString().PadLeft(4)}%\t{gpu.Value.FanSpeed.ToString().PadLeft(3)}%\t{(GPUPerfState == GPUPerformanceState.Auto ? Enum.GetName(gpu.Value.State) : Enum.GetName(GPUPerfState))}");
            }
            sb.AppendLine($"\nFan Speeds: ");
            sb.AppendLine($"\t{"Name".PadRight(10, ' ')}{"Speed".PadLeft(12, ' ')}");
            sb.AppendLine($"\t{"Chassis".PadRight(10, ' ')}{serverStats.ChassisFanSpeedPct.ToString().PadLeft(11, ' ')}%");
            foreach (KeyValuePair<string, uint> fan in serverStats.ChassisFansRPM)
            {
                sb.AppendLine($"\t{fan.Key.PadRight(10, ' ')}{fan.Value.ToString().PadLeft(8, ' ')} RPM");
            }
            sb.AppendLine($"Fan speed resets at {new DateTime(serverStats.ChassisFanSpinDownAt).ToString()}");
            return sb.ToString();
        }


        public static string Reset()
        {
            serverStats.ChassisFanSpinDownAt = DateTime.Now.Ticks;
            return "Resetting cooldown timer.";
        }

        public static string SetTimeout(string time)
        {
            if (time.Contains(':'))
            {
                // hh:mm
                string[] parts = time.Split(':');
                if (parts.Length == 2)
                {
                    int hours = 0, minutes = 0;
                    if (int.TryParse(parts[0], out hours) && int.TryParse(parts[1], out minutes))
                    {
                        DateTime endDate = DateTime.Now.AddMinutes((hours * 60) + minutes);
                        serverStats.ChassisFanSpinDownAt = endDate.Ticks;
                        return $"Fan speed timeout will expire at {endDate}";
                    }
                }
            }
            else
            {
                int minutes = 0;
                if (int.TryParse(time, out minutes))
                {
                    DateTime endDate = DateTime.Now.AddMinutes(minutes);
                    serverStats.ChassisFanSpinDownAt = endDate.Ticks;
                    return $"Fan speed timeout will expire at {endDate}";
                }
            }
            return "Invalid timeout set";
        }

        //public static string GPUSleepAll()
        //{
        //    try
        //    {
        //        PhysicalGPUHandle[] handles = GPUApi.EnumTCCPhysicalGPUs();
        //        foreach (PhysicalGPUHandle ph in handles)
        //        {
        //            GPUApi.SetForcePstate(ph, 8, 2);
        //        }
        //        GPUPerfState = GPUPerformanceState.Low;
        //        return "Setting all GPUs to a low power state";
        //    }
        //    catch (Exception ex)
        //    {
        //        return $"Error when setting GPUs into a low power state: {ex.Message}";
        //    }
        //}

        //public static string GPUWakeAll()
        //{
        //    try
        //    {
        //        PhysicalGPUHandle[] handles = GPUApi.EnumTCCPhysicalGPUs();
        //        foreach (PhysicalGPUHandle ph in handles)
        //        {
        //            GPUApi.SetForcePstate(ph, 16, 2);
        //        }
        //        GPUPerfState = GPUPerformanceState.High;
        //        return "Restoring all GPUs to the default power state";
        //    }
        //    catch (Exception ex)
        //    {
        //        return $"Error when restoring all GPUs to the default power state: {ex.Message}";
        //    }
        //}

        public static bool GPUSleep(uint index)
        {
            PhysicalGPUHandle[] handles = GPUApi.EnumTCCPhysicalGPUs();
            if (index < handles.Length)
            {
                if (!System.Diagnostics.Debugger.IsAttached)
                {
                    GPUApi.SetForcePstate(handles[index], 8, 2);
                }
                string logName = $"gpu.{gpuPerf[index].Name.Replace(" ", "").Replace("_", "")}.{index}.".ToLower();
                NewRelicMain.Log(new Metric() { name = logName + "pstate", value = 8 });
                return true;
            }
            return false;
        }
        public static bool GPUWake(uint index)
        {
            PhysicalGPUHandle[] handles = GPUApi.EnumTCCPhysicalGPUs();
            if (index < handles.Length)
            {
                if (!System.Diagnostics.Debugger.IsAttached)
                {
                    GPUApi.SetForcePstate(handles[index], 16, 2);
                }
                string logName = $"gpu.{gpuPerf[index].Name.Replace(" ", "").Replace("_", "")}.{index}.".ToLower();
                NewRelicMain.Log(new Metric() { name = logName + "pstate", value = 1 });
                return true;
            }
            return false;
        }

        public static string SetChassisFanSpeed(uint newSpeed)
        {
            if (newSpeed < 0)
                newSpeed = 0;
            if (newSpeed > 100)
                newSpeed = 100;
            if (!System.Diagnostics.Debugger.IsAttached)
            {
                List<string> result = SharedContext.ExecuteCLI(SharedContext.Instance.GetConfig().IPMIPath, $"-I {SharedContext.Instance.GetConfig().IPMIInterface}{SharedContext.Instance.GetConfig().IPMILogin} raw 0x30 0x30 0x02 0xff 0x{newSpeed.ToString("x2")}");
            }
            serverStats.ChassisFanSpeedPct = newSpeed;
            serverStats.ChassisFanSpinDownAt = DateTime.Now.AddSeconds(SharedContext.Instance.GetConfig().FanSpinDownDelay).Ticks;
            NewRelicMain.Log(new Metric() { name = "chassis.fans.speed", value = newSpeed });
            return $"Set fan speed to {newSpeed}%";
        }

        public static string SetGPUFanSpeed(uint index, uint newSpeed)
        {
            try
            {
                PhysicalGPUHandle[] handles = GPUApi.EnumTCCPhysicalGPUs();
                if (index < handles.Length)
                {
                    try
                    {
                        uint coolerID = GPUApi.GetClientFanCoolersControl(handles[index]).FanCoolersControlEntries.FirstOrDefault().CoolerId;
                        if (!System.Diagnostics.Debugger.IsAttached)
                        {
                            GPUApi.SetClientFanCoolersControl(handles[index], new PrivateFanCoolersControlV1(new FanCoolersControlEntry[] { new PrivateFanCoolersControlV1.FanCoolersControlEntry(coolerID, NvAPIWrapper.Native.GPU.FanCoolersControlMode.Manual, Math.Min((uint)newSpeed, 100)) }));
                        }
                        gpuPerf[index].RequestedFanSpeed = newSpeed;
                        return $"GPU_{index} fan speed set to {newSpeed}";
                        //Console.WriteLine("Set card fan speed");
                        //Console.WriteLine(GPUApi.GetClientFanCoolersControl(ph).FanCoolersControlEntries[0].Level);
                    }
                    catch (Exception ex) {
                        return $"GPU_{index} does not support setting fan speed.";
                    }
                }
            }
            catch (Exception ex) { SharedContext.Instance.Log(LogLevel.ERR, "HardwareMain", ex.Message); }
            return "";
        }

        public static void Exit()
        {
            // restore the fan states so that nothing catches fire
            PhysicalGPUHandle[] handles = GPUApi.EnumTCCPhysicalGPUs();
            foreach (PhysicalGPUHandle ph in handles)
            {
                try
                {
                    if (!System.Diagnostics.Debugger.IsAttached)
                    {
                        uint coolerID = GPUApi.GetClientFanCoolersControl(ph).FanCoolersControlEntries.FirstOrDefault().CoolerId;
                        GPUApi.SetClientFanCoolersControl(ph, new PrivateFanCoolersControlV1(new FanCoolersControlEntry[] { new PrivateFanCoolersControlV1.FanCoolersControlEntry(coolerID, NvAPIWrapper.Native.GPU.FanCoolersControlMode.Auto, 50) }));

                        // set their performance mode back to whatever
                        GPUApi.SetForcePstate(ph, 16, 2);
                    }
                }
                catch (Exception ex) { }
            }
            if (!System.Diagnostics.Debugger.IsAttached)
            {
                // On the way out, set the fans to high speed to cool the GPUs as it boots again
                SetChassisFanSpeed(100);
                SharedContext.Instance.Log(LogLevel.INFO, "HardwareMain", "Set chassis fan speed to 100%.");
                //SharedContext.ExecuteCLI(SharedContext.Instance.GetConfig().IPMIPath, $"-I {SharedContext.Instance.GetConfig().IPMIInterface}{SharedContext.Instance.GetConfig().IPMILogin} raw 0x30 0x30 0x01 0x01");
                //SharedContext.ExecuteCLI(SharedContext.Instance.GetConfig().IPMIPath, $"-I {SharedContext.Instance.GetConfig().IPMIInterface}{SharedContext.Instance.GetConfig().IPMILogin} raw 0x30 0xce 0x00 0x16 0x05 0x00 0x00 0x00 0x05 0x00 0x01 0x00 0x00");
            }
            //SharedContext.Instance.Log(LogLevel.INFO, "HardwareMain", "Disabled manual control and restored PCIe cooling response.");
        }
    }
}
