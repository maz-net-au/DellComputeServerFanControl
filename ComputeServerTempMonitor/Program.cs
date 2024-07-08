using Newtonsoft.Json;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices.Marshalling;

namespace ComputeServerTempMonitor;

// TODO:
// idle detection (based on power usage? i still want to try and run things cooler)
// 3d print a guide for air through the T4's

class Program
{
    static void Log(string message)
    {
        Console.WriteLine(message);
    }

    static void Main(string[] args)
    {
        string newMode = "x";
        string mode = ""; // default to displaying temps

        CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
        bool IsRunning = true;
        int maxCPU = 0;
        int maxGPU = 0;
        int currentFanSpeed = 0;

        //Dictionary<string, string> currentStats = new Dictionary<string, string>();
        Dictionary<string, string> cpuTemps = new Dictionary<string, string>();
        Dictionary<string, string> gpuTemps = new Dictionary<string, string>();
        Dictionary<string, string> fanDetails = new Dictionary<string, string>();

        // read config for rules
        Config config;
        if (File.Exists("config.json"))
        {
            Log("Loading config.json");
            config = JsonConvert.DeserializeObject<Config>(File.ReadAllText("config.json")) ?? new Config();
        }
        else
        {
            Log("config.json not found. Creating default config.");
            config = new Config();
            File.WriteAllText("config.json", JsonConvert.SerializeObject(config));
        }
        
        if (config.IPMIPath == "")
        {
            Log("No path set to IPMItool. Cannot proceed.");
            Log("Press any key to exit");
            Console.ReadKey();
            return;
        }

        long spinDown = DateTime.Now.AddSeconds(config.FanSpinDownDelay).Ticks;

        Action<bool> printState = (incHeaders) =>
        {
            string headers = "";
            string values = "";

            foreach (string s in cpuTemps.Keys)
            {
                headers += $"{s}\t";
                values += $"{cpuTemps[s]}\t";
            }
            foreach (string s in gpuTemps.Keys)
            {
                headers += $"{s}\t";
                values += $"{gpuTemps[s]}\t";
            }
            foreach (string s in fanDetails.Keys)
            {
                headers += $"{s}\t";
                values += $"{fanDetails[s]}\t";
            }
            if (incHeaders)
            {
                Log(headers);
            }
            Log(values);
        };

        // on startup, call IPMI commands to disable PCIe card response
        List<string> result = exec(config.IPMIPath, $"-I {config.IPMIInterface}{config.IPMILogin} raw 0x30 0xce 0x00 0x16 0x05 0x00 0x00 0x00 0x05 0x00 0x00 0x00 0x00");
        // set fan manual mode
        result = exec(config.IPMIPath, $"-I {config.IPMIInterface}{config.IPMILogin} raw 0x30 0x30 0x01 0x00");
        // set default rate
        result = exec(config.IPMIPath, $"-I {config.IPMIInterface}{config.IPMILogin} raw 0x30 0x30 0x02 0xff 0x{config.DefaultFanSpeed.ToString("x2")}");
        currentFanSpeed = config.DefaultFanSpeed;
        Log("Controlling fans");
        // in a task
        if (config.SMIPath != "")
        {

            Task tGpu = new Task(() =>
            {
                while (IsRunning)
                {
                    try
                    {
                        // set up a process with an outputdatareceived callback
                        List<string> smiOut = exec(config.SMIPath, "dmon -s=p -c=1");
                        // call nvidia-smi with dmon and -s=p
                        // begin read line
                        // ignore lines that start with # (unless i need headings?)
                        // read line, parse items. ID, Power, Core temp, null
                        int newMaxGpu = 0;
                        foreach (string line in smiOut)
                        {
                            string l = line.Trim();
                            if (l == "" || l.StartsWith("#"))
                                continue;
                            string[] gpuD = l.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                            // which is the temp?
                            if (gpuD.Length >= 3)
                            {
                                int temp = int.Parse(gpuD[2]);
                                gpuTemps[$"GPU_{gpuD[0]}"] = temp.ToString();
                                newMaxGpu = Math.Max(newMaxGpu, temp);
                            }
                        }
                        // tab seperated?
                        maxGPU = newMaxGpu;
                        // poll

                        Thread.Sleep(config.GPUCheckingInterval * 1000);
                    }
                    catch (Exception ex)
                    {
                        Log("Error reading GPU temps: " + ex.ToString());
                    }
                }
            }, cancellationTokenSource.Token);
            tGpu.Start();
        }
        // in a task
        Task tCpu = new Task(() =>
        {
            while (IsRunning)
            {
                try
                {
                    List<string> ipmiOut = exec(config.IPMIPath, $"-I {config.IPMIInterface}{config.IPMILogin} sdr type Temperature");
                    // call IPMI commands to get cpu temps
                    int newMaxCpu = 0;
                    int cpuNum = 0;
                    foreach (string line in ipmiOut)
                    {
                        string l = line.Trim();
                        if (l == "" || l.StartsWith("#"))
                            continue;
                        string[] cpuD = l.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        // which is the temp?
                        if (cpuD.Length >= 3)
                        {
                            if (cpuD[0] == "Temp")
                            {
                                int temp = int.Parse(cpuD[4].Replace(" degrees C", ""));
                                cpuTemps[$"CPU_{cpuNum}"] = temp.ToString();
                                newMaxCpu = Math.Max(newMaxCpu, temp);
                                cpuNum++;
                            }
                        }
                    }
                    // tab seperated?
                    maxCPU = newMaxCpu;
                    // poll
                    Thread.Sleep(config.CPUCheckingInterval * 1000);
                }
                catch (Exception ex)
                {
                    Log("Error reading CPU temps: " + ex.ToString());
                }
            }
        }, cancellationTokenSource.Token);
        tCpu.Start();

        // fan control thread
        Task tFan = new Task(() =>
        {
            while (IsRunning)
            {
                try
                {
                    int newSpeed = 0;
                    // watch for cpu cores + gpu cores.
                    foreach (FanTempSpeeds fts in config.CPULimits)
                    {
                        if (maxCPU >= fts.Temp)
                            newSpeed = Math.Max(newSpeed, fts.MinSpeed);
                    }
                    foreach (FanTempSpeeds fts in config.GPULimits)
                    {
                        if (maxGPU >= fts.Temp)
                            newSpeed = Math.Max(newSpeed, fts.MinSpeed);
                    }
                    fanDetails["Fan_%"] = currentFanSpeed.ToString();
                    fanDetails["To_%"] = newSpeed.ToString();
                    if (newSpeed >= currentFanSpeed)
                    {
                        // once fans ramp, set a timer to delay the spin-down response until idle for a long time
                        spinDown = DateTime.Now.AddSeconds(config.FanSpinDownDelay).Ticks;
                    }
                    if (newSpeed > currentFanSpeed || DateTime.Now.Ticks > spinDown)
                    {
                        // call IPMI commands to set fan speed
                        result = exec(config.IPMIPath, $"-I {config.IPMIInterface}{config.IPMILogin} raw 0x30 0x30 0x02 0xff 0x{newSpeed.ToString("x2")}");
                        currentFanSpeed = newSpeed;
                        printState(true);
                    }
                    fanDetails["Until"] = new DateTime(spinDown).ToString();
                }
                catch (Exception ex)
                {
                    Log("Error processing fan speeds: " + ex.ToString());
                }
            }
        }, cancellationTokenSource.Token);
        tFan.Start();

        // put in our external control loop here
        // it'll be a state-machine with the current mode

        while (IsRunning)
        {
            try
            {
                if (Console.KeyAvailable)
                {
                    ConsoleKeyInfo key = Console.ReadKey(true);
                    newMode = key.KeyChar.ToString().ToLower();
                    if (newMode == mode)
                    {
                        continue;
                    }
                    mode = newMode;
                    /*
                        "d" to dispay current state. press any key to stop
                        "s" to set a new fan speed
                        "r" to reset, and ignore the cooldown timer
                        "f" for finished, it puts the GPUs into an idle state (P8 or P12 if possible), waits some time and then slows the fans
                        "w" for wake, puts the GPUs into P0
                    */
                    switch (mode)
                    {
                        case "d":
                            Log("Dispaying current state. Press any key to cancel.");
                            int headerRow = 0;
                            while (!Console.KeyAvailable)
                            {
                                printState(headerRow % 25 == 0);
                                headerRow++;
                                Thread.Sleep(1000);
                            }
                            break;
                        case "s":
                            Log("Setting a new fan speed. Enter a number between 0 and 100");
                            string rIn = Console.ReadLine() ?? "";
                            int newSpeed = 0;
                            if (int.TryParse(rIn, out newSpeed))
                            {
                                if (newSpeed < 0)
                                    newSpeed = 0;
                                if (newSpeed > 100)
                                    newSpeed = 100;
                                Log($"Setting fan speed to {newSpeed}%");
                                result = exec(config.IPMIPath, $"-I {config.IPMIInterface}{config.IPMILogin} raw 0x30 0x30 0x02 0xff 0x{newSpeed.ToString("x2")}");
                                currentFanSpeed = newSpeed;
                                spinDown = DateTime.Now.AddSeconds(config.FanSpinDownDelay).Ticks;
                            }
                            else
                            {
                                Log($"Invalid speed entered: '{rIn}'");
                            }
                            break;
                        case "r":
                            Log("Resetting cooldown timer.");
                            spinDown = DateTime.Now.Ticks;
                            break;
                        case "t":
                            Log("Enter a new delay as h:mm or mmm.");
                            string time = Console.ReadLine() ?? "";
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
                                        spinDown = endDate.Ticks;
                                        Log($"Fan speed timeout will expire at {endDate}");
                                    }
                                }
                            }
                            else
                            {
                                int minutes = 0;
                                if (int.TryParse(time, out minutes))
                                {
                                    DateTime endDate = DateTime.Now.AddMinutes(minutes);
                                    spinDown = endDate.Ticks;
                                    Log($"Fan speed timeout will expire at {endDate}");
                                }
                            }
                            break;
                        case "c":
                            if (File.Exists("config.json"))
                            {
                                config = JsonConvert.DeserializeObject<Config>(File.ReadAllText("config.json")) ?? new Config();
                                Log("config.json loaded");
                                Log(JsonConvert.SerializeObject(config, Formatting.Indented));
                            }
                            break;
                        //case "f":
                        //    Log("Putting all the GPUs into a low power state.");
                        //    // P8? P12?
                        //    try
                        //    {
                        //        PhysicalGPUHandle[] handles = GPUApi.EnumTCCPhysicalGPUs();
                        //        foreach (PhysicalGPUHandle ph in handles)
                        //        {
                        //            IPerformanceStates20Info pi = GPUApi.GetPerformanceStates20(ph);
                        //            var lowPowerState = new PState(pi, NvAPIWrapper.Native.GPU.PerformanceStateId.P8_HDVideoPlayback);
                        //            Console.WriteLine(JsonConvert.SerializeObject(lowPowerState));
                        //            NvAPIWrapper.GPU.PhysicalGPU[] gpus = NvAPIWrapper.GPU.PhysicalGPU.GetTCCPhysicalGPUs();
                        //            foreach (NvAPIWrapper.GPU.PhysicalGPU gpu in gpus)
                        //            {
                        //                gpu.
                        //            }
                        //            GPUApi.SetPerformanceStates20(ph, pi);
                        //        }
                        //    }
                        //    catch (Exception ex)
                        //    {
                        //        Log(ex.ToString());
                        //    }
                        //    break;
                        //case "v":
                        //    try
                        //    {
                        //        PhysicalGPUHandle[] handles = GPUApi.EnumTCCPhysicalGPUs();
                        //        foreach (PhysicalGPUHandle ph in handles)
                        //        {
                        //            IPerformanceStates20Info pi = GPUApi.GetPerformanceStates20(ph);
                        //            Console.WriteLine(JsonConvert.SerializeObject(pi));
                        //        }
                        //    }
                        //    catch (Exception ex)
                        //    {
                        //        Log(ex.ToString());
                        //    }
                        //    break;
                        //case "w":
                        //    Log("Restoring all the GPU to a high power state.");
                        //    try
                        //    {
                        //        PhysicalGPUHandle[] handles2 = GPUApi.EnumTCCPhysicalGPUs();
                        //        foreach (PhysicalGPUHandle ph in handles2)
                        //        {
                        //            IPerformanceStates20Info pi = GPUApi.GetPerformanceStates20(ph);
                        //            GPUApi.SetPerformanceStates20(ph, pi); // restore all states? or did i just overwrite these until I reboot?
                        //        }
                        //    }
                        //    catch (Exception ex)
                        //    {
                        //        Log(ex.ToString());
                        //    }
                        //    break;
                        case "x":
                            IsRunning = false;
                            // restore the fan states so that nothing catches fire
                            exec(config.IPMIPath, $"-I {config.IPMIInterface}{config.IPMILogin} raw 0x30 0x30 0x01 0x01");
                            Log("Disabled manual control.");
                            exec(config.IPMIPath, $"-I {config.IPMIInterface}{config.IPMILogin} raw 0x30 0xce 0x00 0x16 0x05 0x00 0x00 0x00 0x05 0x00 0x01 0x00 0x00");
                            Log("Restored PCIe cooling response.");
                            break;
                        default:
                            Log("'d' to dispay current state. press any key to stop");
                            Log("'s' to set a new fan speed.");
                            Log("'r' to reset, and ignore the cooldown timer.");
                            Log("'c' to reload the configuration file.");
                            Log("'t' to set a new cooldown delay.");
                            //Log("'f' for finished, it puts the GPUs into an idle state(P8 or P12 if possible), waits some time and then slows the fans.");
                            //Log("'w' for wake, which restores the high power states.");
                            Log("'x' to exit");
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log("In input loop: " + ex.ToString());
            }
        }
        cancellationTokenSource.Cancel();
    }


    static List<string> exec(string command, string args)
    {
        List<string> retMessage = new List<string>();
        ProcessStartInfo startInfo = new ProcessStartInfo();
        Process p = new Process();

        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardInput = true;

        startInfo.WorkingDirectory = Path.GetDirectoryName(command) ?? "";
        startInfo.UseShellExecute = false;
        startInfo.Arguments = args;
        startInfo.FileName = command;

        p.StartInfo = startInfo;
        p.OutputDataReceived += (sender, line) =>
        {
            retMessage.Add(line.Data ?? "");
        };

        p.Start();
        p.BeginOutputReadLine();
        p.WaitForExit();
        p.CancelOutputRead();

        return retMessage;
    }
}
