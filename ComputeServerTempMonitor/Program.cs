using Newtonsoft.Json;
using System;
using System.Diagnostics;

namespace ComputeServerTempMonitor;

class Program
{

    // TODO
    // try catch in tasks
    // try catch control loop
    // log temps cpu0, cpu1, gpu0, gpu1, currentFanSpeed, targetFanSpeed, reset datetime
    static void Log(string message)
    {
        Console.WriteLine(message);
    }

    static void Main(string[] args)
    {
        CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
        bool IsRunning = true;
        int maxCPU = 0;
        int maxGPU = 0;
        int currentFanSpeed = 0;

        Dictionary<string, string> currentStats = new Dictionary<string, string>();

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
                                currentStats[$"GPU_{gpuD[0]}"] = temp.ToString();
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
                                currentStats[$"CPU_{cpuNum}"] = temp.ToString();
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
        int headerRow = 0;
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
                currentStats["Fan_%"] = currentFanSpeed.ToString();
                currentStats["To_%"] = newSpeed.ToString();
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
                }
                currentStats["Until"] = new DateTime(spinDown).ToString();
                string headers = "";
                string values = "";
                foreach (string s in new List<string>() { "CPU_0", "CPU_1", "GPU_0", "GPU_1", "Fan_%", "To_%", "Until"})
                {
                    headers += $"{s}\t";
                    values += $"{(currentStats.ContainsKey(s) ? currentStats[s] : "-")}\t";
                }
                if (headerRow == 0)
                {
                    Log(headers);
                }
                Log(values);
                headerRow = (headerRow + 1) % 20;
                Thread.Sleep(1000);
            }
            catch (Exception ex)
            {
                Log("Error setting fan speeds: " + ex.ToString());
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