using ComputeServerTempMonitor.ComfyUI;
using Discord;
using Discord.Net;
using Discord.Rest;
using Discord.WebSocket;
using Newtonsoft.Json;
using NvAPIWrapper.Native;
using NvAPIWrapper.Native.GPU.Structures;
using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using static NvAPIWrapper.Native.GPU.Structures.PrivateFanCoolersControlV1;

namespace ComputeServerTempMonitor;

// TODO:
// llama.cpp support for my discord bot -> stand-alone exe that the bot talks to?
// Or just investigate the oobabooga API
//  - confirm if CUDA_VISIBLE_DEVICES works with llama sharp
//  - A slash command to start a new conversation. takes a system prompt as input?
//  - Replies come back in a thread. posts in that thread are automatically added as part of the conversation
//  - A button to re-generate the last response?
//  - a command or button that pops up a modal to replace the last bot response with something else
//  - a continue button? would I be better off just limiting responses to 200 chars and making continue work?
//  - check if responses can be edited by the bot (so i can send partial replies and update them)
// report cpu utilisation
// search for running applications when starting and connect to those?
// replace gpuTemps and cpuTemps with a more general stats object
// serialise out comfyUI prompt requests and re-load on start (lets the regenerate button work after a restart)
// refactor this complete dog's breakfast of a hack job.
// stats on commands used per user
// return comfyUI queue length

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
}

class Program
{
    private static DiscordSocketClient _client;
    static int maxCPU = 0;
    static int maxGPU = 0;
    static int currentFanSpeed = 0;
    static long spinDown = 0;
    static bool IsRunning = true;
    static GPUPerformanceState GPUPerfState = GPUPerformanceState.Auto;

    //static Dictionary<string, string> currentStats = new Dictionary<string, string>();
    static Dictionary<string, string> cpuTemps = new Dictionary<string, string>();
    //static Dictionary<string, string> gpuTemps = new Dictionary<string, string>();
    static Dictionary<int, GPUStats> gpuPerf = new Dictionary<int, GPUStats>();
    static Dictionary<string, string> fanDetails = new Dictionary<string, string>();
    static Dictionary<string, SoftwareRef> programs = new Dictionary<string, SoftwareRef>();

    // read config for rules
    public static Config config;
    public static DiscordMeta discordInfo;

    //static void Log(string message)
    //{
    //    Console.WriteLine(message);
    //}

    private static Task Log(LogMessage msg)
    {
        Console.WriteLine(msg.ToString());
        return Task.CompletedTask;
    }

    static string WriteUsers()
    {
        File.WriteAllText("users.json", JsonConvert.SerializeObject(discordInfo, Formatting.Indented));
        return "Users file updated";
    }

    public static string LoadUsers()
    {
        if (File.Exists("users.json"))
        {
            discordInfo = JsonConvert.DeserializeObject<DiscordMeta>(File.ReadAllText("users.json")) ?? new DiscordMeta();
            // for the comfyUI block, we need to load the model list
            // we might need to do something for an image picker as well, though that's harder to show examples
            // the list is too long anyway. i should take a url for new ones and put them in a temp folder. watch the size

            return "users file loaded";
        }
        return "users file not found.";
    }
    static void CurrentDomain_ProcessExit(object sender, EventArgs e)
    {
        ComfyMain.SaveCache();
    }

    static async Task Main(string[] args)
    {
        string newMode = "x";
        string mode = ""; // default to displaying temps
        AppDomain.CurrentDomain.ProcessExit += new EventHandler(CurrentDomain_ProcessExit);
        CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();


        if (File.Exists("config.json"))
        {
            await Log(new LogMessage(LogSeverity.Info, "Main", "Loading config.json"));
            config = JsonConvert.DeserializeObject<Config>(File.ReadAllText("config.json")) ?? new Config();
        }
        else
        {
            await Log(new LogMessage(LogSeverity.Info, "Main", "config.json not found. Creating default config."));
            config = new Config();
            File.WriteAllText("config.json", JsonConvert.SerializeObject(config));
        }

        if (File.Exists("users.json"))
        {
            await Log(new LogMessage(LogSeverity.Info, "Main", "Loading users.json"));
            discordInfo = JsonConvert.DeserializeObject<DiscordMeta>(File.ReadAllText("users.json")) ?? new DiscordMeta();
        }
        else
        {
            await Log(new LogMessage(LogSeverity.Info, "Main", "users.json not found. Creating default users file."));
            discordInfo = new DiscordMeta();
            File.WriteAllText("users.json", JsonConvert.SerializeObject(discordInfo));
        }

        if (config.IPMIPath == "")
        {
            await Log(new LogMessage(LogSeverity.Info, "Main", "No path set to IPMItool. Cannot proceed."));
            await Log(new LogMessage(LogSeverity.Info, "Main", "Press any key to exit"));
            Console.ReadKey();
            return;
        }

        // today we're not wanting to test discord bot features. just get the users set up
        if (!System.Diagnostics.Debugger.IsAttached)
        {
            _client = new DiscordSocketClient();
            _client.Log += Log;
            _client.Ready += Client_Ready;
            _client.SlashCommandExecuted += SlashCommandHandler;
            _client.ButtonExecuted += ButtonExecuted;
            _client.SelectMenuExecuted += SelectMenuExecuted;
            await _client.LoginAsync(TokenType.Bot, config.DiscordBotToken);
            await _client.StartAsync();
        }

        ComfyMain.LoadCache();

        spinDown = DateTime.Now.AddSeconds(config.FanSpinDownDelay).Ticks;

        Action<bool> printState = (incHeaders) =>
        {
            string headers = "";
            string values = "";

            foreach (string s in cpuTemps.Keys)
            {
                headers += $"{s}\t";
                values += $"{cpuTemps[s]}\t";
            }
            foreach (int s in gpuPerf.Keys)
            {
                headers += $"GPU_{s}\t";
                values += $"{gpuPerf[s].LastTemp}\t";
            }
            foreach (string s in fanDetails.Keys)
            {
                headers += $"{s}\t";
                values += $"{fanDetails[s]}\t";
            }
            if (incHeaders)
            {
                Console.WriteLine(headers);
                //Log(new LogMessage(LogSeverity.Info, "Main", headers));
            }
            Console.WriteLine(values);
            //Log(new LogMessage(LogSeverity.Info, "Main", values));
        };

        if (!System.Diagnostics.Debugger.IsAttached)
        {
            // on startup, call IPMI commands to disable PCIe card response
            List<string> result = exec(config.IPMIPath, $"-I {config.IPMIInterface}{config.IPMILogin} raw 0x30 0xce 0x00 0x16 0x05 0x00 0x00 0x00 0x05 0x00 0x00 0x00 0x00");
            // set fan manual mode
            result = exec(config.IPMIPath, $"-I {config.IPMIInterface}{config.IPMILogin} raw 0x30 0x30 0x01 0x00");
            // set default rate
            //result = exec(config.IPMIPath, $"-I {config.IPMIInterface}{config.IPMILogin} raw 0x30 0x30 0x02 0xff 0x{config.DefaultFanSpeed.ToString("x2")}");
            SetFanSpeed(config.DefaultFanSpeed);
            currentFanSpeed = config.DefaultFanSpeed;
            await Log(new LogMessage(LogSeverity.Info, "Main", "Controlling fans"));
            foreach (KeyValuePair<string, SoftwareRef> p in config.Software)
            {
                await Log(new LogMessage(LogSeverity.Info, "Startup", ConnectToProgram("Administrator:  " + p.Key)));
            }

            // in a task
            if (config.SMIPath != "")
            {
                Task tGpu = new Task(async () =>
                {
                    while (IsRunning)
                    {
                        try
                        {
                            int newMaxGpu = 0;
                            // because there seems to be no way to link nvidia-smi output and physicallTCC cards from nvapi,
                            //  I have to do everything via nvapi because I need that one for setting pstates.
                            PhysicalGPUHandle[] handles = GPUApi.EnumTCCPhysicalGPUs();
                            for (int i = 0; i < handles.Length; i++)
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
                                newMaxGpu = Math.Max(newMaxGpu, gpuPerf[i].LastTemp);
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
                            }
                            maxGPU = newMaxGpu;

                            Thread.Sleep(config.GPUCheckingInterval * 1000);
                        }
                        catch (Exception ex)
                        {
                            await Log(new LogMessage(LogSeverity.Error, "Main", "Error reading GPU temps: " + ex.Message, ex));
                        }
                    }
                }, cancellationTokenSource.Token);
                tGpu.Start();
            }
            // in a task
            Task tCpu = new Task(async () =>
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
                        await Log(new LogMessage(LogSeverity.Error, "Main", "Error reading CPU temps: " + ex.Message, ex));
                    }
                }
            }, cancellationTokenSource.Token);
            tCpu.Start();

            Task tPerf = new Task(async () =>
            {
                while (IsRunning)
                {
                    try
                    {
                        if (GPUPerfState == GPUPerformanceState.Auto)
                        {
                            foreach (int id in gpuPerf.Keys)
                            {
                                if (gpuPerf[id].LastUtilisation >= config.GPUAutoPerfThreshold)
                                {
                                    // compute go brrrrrr
                                    if (gpuPerf[id].State != GPUPerformanceState.High)
                                    {
                                        if (GPUWake(id))
                                        {
                                            gpuPerf[id].State = GPUPerformanceState.High;
                                            await Log(new LogMessage(LogSeverity.Info, "PerformanceTask", $"GPU {id} entered a high performance state"));
                                        }
                                    }
                                    long newTimeout = DateTime.Now.AddSeconds(config.GPUAutoPerfTimeout).Ticks;
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
                                                await Log(new LogMessage(LogSeverity.Info, "PerformanceTask", $"GPU {id} entered a idle state"));
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        Thread.Sleep(1000);
                    }
                    catch (Exception ex)
                    {

                    }
                }
            }, cancellationTokenSource.Token);
            tPerf.Start();

            // fan control thread
            Task tFan = new Task(async () =>
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
                        fanDetails["To__%"] = newSpeed.ToString();
                        if (newSpeed >= currentFanSpeed)
                        {
                            // once fans ramp, set a timer to delay the spin-down response until idle for a long time
                            // dont reduce / reset any extended timeout
                            if (spinDown < DateTime.Now.AddSeconds(config.FanSpinDownDelay).Ticks)
                                spinDown = DateTime.Now.AddSeconds(config.FanSpinDownDelay).Ticks;
                        }
                        if (newSpeed > currentFanSpeed || DateTime.Now.Ticks > spinDown)
                        {
                            // call IPMI commands to set fan speed
                            //result = exec(config.IPMIPath, $"-I {config.IPMIInterface}{config.IPMILogin} raw 0x30 0x30 0x02 0xff 0x{newSpeed.ToString("x2")}");
                            SetFanSpeed(newSpeed);
                            currentFanSpeed = newSpeed;
                            await Log(new LogMessage(LogSeverity.Error, "FanTask", $"Fan speed is now {newSpeed}% until {new DateTime(spinDown).ToString()}"));
                            //printState(true);
                        }
                        fanDetails["Until"] = new DateTime(spinDown).ToString();
                    }
                    catch (Exception ex)
                    {
                        await Log(new LogMessage(LogSeverity.Error, "FanTask", "Error processing fan speeds: " + ex.Message));
                    }
                }
            }, cancellationTokenSource.Token);
            tFan.Start();
        }
        // put in our external control loop here
        // it'll be a state-machine with the current mode
        bool lastKeyDefault = false;
        while (IsRunning)
        {
            try
            {
                if (Console.KeyAvailable)
                {
                    ConsoleKeyInfo key = Console.ReadKey(true);
                    mode = key.KeyChar.ToString().ToLower();
                    switch (mode)
                    {
                        case "d":
                            await Log(new LogMessage(LogSeverity.Info, "Main", "Dispaying current state. Press any key to cancel."));
                            int headerRow = 0;
                            while (!Console.KeyAvailable)
                            {
                                printState(headerRow % 25 == 0);
                                headerRow++;
                                Thread.Sleep(5000);
                            }
                            Console.ReadKey(true); // discard the key we got
                            lastKeyDefault = false;
                            break;
                        case "s":
                            await Log(new LogMessage(LogSeverity.Info, "Main", "Setting a new fan speed. Enter a number between 0 and 100"));
                            string rIn = Console.ReadLine() ?? "";
                            int newSpeed = 0;
                            if (int.TryParse(rIn, out newSpeed))
                            {
                                await Log(new LogMessage(LogSeverity.Info, "Main", SetFanSpeed(newSpeed)));
                            }
                            else
                            {
                                await Log(new LogMessage(LogSeverity.Info, "Main", $"Invalid speed entered: '{rIn}'"));
                            }
                            lastKeyDefault = false;
                            break;
                        case "r":
                            await Log(new LogMessage(LogSeverity.Info, "Main", Reset()));
                            lastKeyDefault = false;
                            break;
                        case "t":
                            await Log(new LogMessage(LogSeverity.Info, "Main", "Enter a new delay as h:mm or mmm."));
                            string time = Console.ReadLine() ?? "";
                            await Log(new LogMessage(LogSeverity.Info, "Main", SetTimeout(time)));
                            lastKeyDefault = false;
                            break;
                        case "c":
                            await Log(new LogMessage(LogSeverity.Info, "Main", LoadConfig()));
                            lastKeyDefault = false;
                            break;
                        case "u":
                            await Log(new LogMessage(LogSeverity.Info, "Main", LoadUsers()));
                            lastKeyDefault = false;
                            break;
                        //case "i":
                        //    await Log(new LogMessage(LogSeverity.Info, "Main", GPUSleepAll()));
                        //    lastKeyDefault = false;
                        //    break;
                        //case "w":
                        //    await Log(new LogMessage(LogSeverity.Info, "Main", GPUWakeAll()));
                        //    lastKeyDefault = false;
                        //    break;
                        //case "q":
                        //    PhysicalGPUHandle[] handles = GPUApi.EnumTCCPhysicalGPUs();
                        //    foreach (PhysicalGPUHandle ph in handles)
                        //    {
                        //        try
                        //        {
                        //            uint coolerID = GPUApi.GetClientFanCoolersControl(ph).FanCoolersControlEntries.FirstOrDefault().CoolerId;
                        //            GPUApi.SetClientFanCoolersControl(ph, new PrivateFanCoolersControlV1(new FanCoolersControlEntry[] { new PrivateFanCoolersControlV1.FanCoolersControlEntry(coolerID, NvAPIWrapper.Native.GPU.FanCoolersControlMode.Manual, 61) }));
                        //            Console.WriteLine("Set fan speed");
                        //            Console.WriteLine(GPUApi.GetClientFanCoolersControl(ph).FanCoolersControlEntries[0].Level);
                        //        }
                        //        catch (Exception ex) { }
                        //    }
                        //    break;
                        case "q":
                            // Get the path of the process you want to find
                            string name = "";
                            Console.WriteLine("Enter your process name:");
                            name = Console.ReadLine();

                            // Get a list of all the processes currently running on the system
                            Process[] processes = Process.GetProcessesByName(name);

                            // Iterate through the list of processes
                            foreach (Process process in processes)
                            {
                                // Check if the process path matches the one we're looking for
                                //if (process.MainModule.FileName == path)
                                //{
                                    // If it does, print the process ID to the console
                                    Console.WriteLine($"PID: {process.Id}, Name: {process.MainModule?.FileName}, {process.ProcessName}, {process.MainWindowTitle}");
                                //}
                            }
                            lastKeyDefault = false;
                            break;
                        //case "a":
                        //    GPUPerfState = GPUPerformanceState.Auto;
                        //    await Log(new LogMessage(LogSeverity.Info, "Main", "Setting all GPUs to auto power state"));
                        //    lastKeyDefault = false;
                        //    break;
                        case "x":
                            await Log(new LogMessage(LogSeverity.Info, "Main", Exit()));
                            lastKeyDefault = false;
                            break;
                        case "z":
                            await SendCommands();
                            await Log(new LogMessage(LogSeverity.Info, "Main", "Discord commands reregistered"));
                            lastKeyDefault = false;
                            break;
                        case "h":
                                await Log(new LogMessage(LogSeverity.Info, "Main", "'d' to dispay current state. press any key to stop"));
                                await Log(new LogMessage(LogSeverity.Info, "Main", "'s' to set a new fan speed."));
                                await Log(new LogMessage(LogSeverity.Info, "Main", "'r' to reset, and ignore the cooldown timer."));
                                await Log(new LogMessage(LogSeverity.Info, "Main", "'c' to reload the configuration file."));
                                await Log(new LogMessage(LogSeverity.Info, "Main", "'u' to reload the users file."));
                                await Log(new LogMessage(LogSeverity.Info, "Main", "'t' to set a new cooldown delay."));
                                //await Log(new LogMessage(LogSeverity.Info, "Main", "'i' to force the GPUs into an idle state."));
                                //await Log(new LogMessage(LogSeverity.Info, "Main", "'w' to restores the GPU high power states."));
                                await Log(new LogMessage(LogSeverity.Info, "Main", "'z' to reregister all discord commands"));
                                await Log(new LogMessage(LogSeverity.Info, "Main", "'x' to exit"));
                            break;
                        default:
                            if (!lastKeyDefault)
                            {
                            }
                            lastKeyDefault = true;
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                await Log(new LogMessage(LogSeverity.Info, "Main", "In input loop: " + ex.Message, ex));
            }
        }
        cancellationTokenSource.Cancel();
    }

    private static async Task SelectMenuExecuted(SocketMessageComponent arg)
    {
        string[] parts = arg.Data.CustomId.Split(":");
        if (parts.Length != 2)
        {
            await arg.RespondAsync($"Invalid command");
            return;
        }
        switch (parts[0])
        {
            case "variation":
                {
                    Task.Run(async () =>
                    {
                        if (arg.ChannelId == null)
                            await arg.RespondAsync("Request failed");
                        IMessageChannel? chan = _client.GetChannel(arg.ChannelId ?? 0) as IMessageChannel;
                        if (chan == null)
                        {
                            await Log(new LogMessage(LogSeverity.Error, "Button", "Channel not found"));
                            await arg.RespondAsync("Request failed");
                        }
                        float vary_by = 0.5f; // default
                        if (!float.TryParse(arg.Data.Values.FirstOrDefault(), out vary_by))
                            return;
                        await arg.RespondAsync($"{Math.Round(vary_by*100)}% variation request accepted.\n{GetDrawStatus()}");
                        HistoryResponse? hr = await ComfyMain.Variation(parts[1], 0, vary_by, arg.User.Username);
                        if (hr == null)
                        {
                            await Log(new LogMessage(LogSeverity.Error, "Button", "Result is null"));
                            await chan.SendMessageAsync("Request failed");
                        }
                        else
                        {
                            DiscordImageResponse res = CreateImageGenResponse(hr, arg.GuildId);
                            await arg.ModifyOriginalResponseAsync((s) =>
                            {
                                s.Content = $"Here is your variation {arg.User.Mention}";
                                s.Attachments = res.Attachments;
                                s.Components = res.Components.Build();
                                s.AllowedMentions = new AllowedMentions(AllowedMentionTypes.Users);
                            });
                            //await chan.SendFilesAsync(res.Attachments, $"I made it bigger for you {arg.User.Mention}", false, null, RequestOptions.Default, AllowedMentions.All, null, res.Components.Build());
                        }
                    });
                }
                break;
        }
        // move upscale here as well?
    }

    private static async Task ButtonExecuted(SocketMessageComponent arg)
    {
        string[] parts = arg.Data.CustomId.Split(":");
        if (parts.Length != 2)
        {
            await arg.RespondAsync($"Invalid command");
            return;
        }
        string[] args = parts[1].Split(",");
        if (discordInfo.CheckPermission(arg.GuildId, arg.User.Id) == AccessLevel.None)
        {
            await arg.RespondAsync($"Button '{parts[0]}' not allowed.");
            return; // failed
        }
        
        switch (parts[0])
        {
            case "upscale":
                {
                    Task.Run(async () =>
                    {
                        if (arg.ChannelId == null)
                            await arg.RespondAsync("Request failed");
                        IMessageChannel? chan = _client.GetChannel(arg.ChannelId ?? 0) as IMessageChannel;
                        if (chan == null)
                        {
                            await Log(new LogMessage(LogSeverity.Error, "Button", "Channel not found"));
                            await arg.RespondAsync("Request failed");
                        }
                        
                        int num;
                        if (!int.TryParse(args[1], out num))
                            return;
                        float upscale_by = 2.0f; // default
                        if (!float.TryParse(args[2], out upscale_by))
                            return;
                        await arg.RespondAsync($"{upscale_by}x upscale request accepted.\n{GetDrawStatus()}");
                        HistoryResponse? hr = await ComfyMain.Upscale(args[0], num, upscale_by, arg.User.Username);
                        if (hr == null)
                        {
                            await Log(new LogMessage(LogSeverity.Error, "Button", "Result is null"));
                            await chan.SendMessageAsync("Request failed");
                        }
                        else
                        {
                            DiscordImageResponse res = CreateImageGenResponse(hr, arg.GuildId);
                            await arg.ModifyOriginalResponseAsync((s) =>
                            {
                                s.Content = $"Here is your upscaled image {arg.User.Mention}";
                                s.Attachments = res.Attachments;
                                s.Components = res.Components.Build();
                                s.AllowedMentions = new AllowedMentions(AllowedMentionTypes.Users);
                            });
                            //await chan.SendFilesAsync(res.Attachments, $"I made it bigger for you {arg.User.Mention}", false, null, RequestOptions.Default, AllowedMentions.All, null, res.Components.Build());
                        }
                    });
                }
                break;
            case "regenerate":
                {
                    Task.Run(async () =>
                    {
                        if (arg.ChannelId == null)
                            await arg.RespondAsync("Request failed");
                        IMessageChannel? chan = _client.GetChannel(arg.ChannelId ?? 0) as IMessageChannel;
                        if (chan == null)
                        {
                            await Log(new LogMessage(LogSeverity.Error, "Button", "Channel not found"));
                            await arg.RespondAsync("Request failed");
                        }
                        await arg.RespondAsync($"Regenerate request accepted.\n{GetDrawStatus()}");
                        HistoryResponse? hr = await ComfyMain.Regenerate(args[0], arg.User.Username);
                        if (hr == null)
                        {
                            await Log(new LogMessage(LogSeverity.Error, "Button", "Result is null"));
                            await chan.SendMessageAsync("Request failed");
                        }
                        else
                        {
                            DiscordImageResponse res = CreateImageGenResponse(hr, arg.GuildId);
                            await arg.ModifyOriginalResponseAsync((s) =>
                            {
                                s.Content = $"Here is your regenerated image {arg.User.Mention}";
                                s.Attachments = res.Attachments;
                                s.Components = res.Components.Build();
                                s.AllowedMentions = new AllowedMentions(AllowedMentionTypes.Users);
                            });
                            //await chan.SendFilesAsync(res.Attachments, $"Is this version better {arg.User.Mention}?", false, null, RequestOptions.Default, AllowedMentions.All, null, res.Components.Build());
                        }
                    });
                }
                break;
            default:
                await arg.RespondAsync($"Unknown command");
                break;
        }
    }

    public static string Exit()
    {
        // quit any running processes?
        IsRunning = false;
        // restore the fan states so that nothing catches fire
        PhysicalGPUHandle[] handles = GPUApi.EnumTCCPhysicalGPUs();
        foreach (PhysicalGPUHandle ph in handles)
        {
            try
            {
                uint coolerID = GPUApi.GetClientFanCoolersControl(ph).FanCoolersControlEntries.FirstOrDefault().CoolerId;
                GPUApi.SetClientFanCoolersControl(ph, new PrivateFanCoolersControlV1(new FanCoolersControlEntry[] { new PrivateFanCoolersControlV1.FanCoolersControlEntry(coolerID, NvAPIWrapper.Native.GPU.FanCoolersControlMode.Auto, 50) }));
            }
            catch (Exception ex) { }
        }
        exec(config.IPMIPath, $"-I {config.IPMIInterface}{config.IPMILogin} raw 0x30 0x30 0x01 0x01");
        exec(config.IPMIPath, $"-I {config.IPMIInterface}{config.IPMILogin} raw 0x30 0xce 0x00 0x16 0x05 0x00 0x00 0x00 0x05 0x00 0x01 0x00 0x00");
        return "Disabled manual control and restored PCIe cooling response.";
    }

    public static string Reset() 
    {
        spinDown = DateTime.Now.Ticks;
        Thread.Sleep(2000);
        return "Resetting cooldown timer.";
    }

    public static string LoadConfig()
    {
        if (File.Exists("config.json"))
        {
            config = JsonConvert.DeserializeObject<Config>(File.ReadAllText("config.json")) ?? new Config();
            // for the comfyUI block, we need to load the model list
            // we might need to do something for an image picker as well, though that's harder to show examples
            // the list is too long anyway. i should take a url for new ones and put them in a temp folder. watch the size

            return "config.json loaded";
        }
        return "Config file not found.";
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
                    spinDown = endDate.Ticks;
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
                spinDown = endDate.Ticks;
                return $"Fan speed timeout will expire at {endDate}";
            }
        }
        return "Invalid timeout set";
    }

    public static string GPUSleepAll()
    {
        try
        {
            PhysicalGPUHandle[] handles = GPUApi.EnumTCCPhysicalGPUs();
            foreach (PhysicalGPUHandle ph in handles)
            {
                GPUApi.SetForcePstate(ph, 8, 2);
            }
            GPUPerfState = GPUPerformanceState.Low;
            return "Setting all GPUs to a low power state";
        }
        catch (Exception ex)
        {
            return $"Error when setting GPUs into a low power state: {ex.Message}";
        }
    }

    public static string GPUWakeAll()
    {
        try
        {
            PhysicalGPUHandle[] handles = GPUApi.EnumTCCPhysicalGPUs();
            foreach (PhysicalGPUHandle ph in handles)
            {
                GPUApi.SetForcePstate(ph, 16, 2);
            }
            GPUPerfState = GPUPerformanceState.High;
            return "Restoring all GPUs to the default power state";
        }
        catch (Exception ex)
        {
            return $"Error when restoring all GPUs to the default power state: {ex.Message}";
        }
    }

    public static bool GPUSleep(int index)
    {
        PhysicalGPUHandle[] handles = GPUApi.EnumTCCPhysicalGPUs();
        if(index < handles.Length)
        {
            GPUApi.SetForcePstate(handles[index], 8, 2);
            return true;
        }
        return false;
    }
    public static bool GPUWake(int index)
    {
        PhysicalGPUHandle[] handles = GPUApi.EnumTCCPhysicalGPUs();
        if (index < handles.Length)
        {
            GPUApi.SetForcePstate(handles[index], 16, 2);
            return true;
        }
        return false;
    }

    public static string SetFanSpeed(int newSpeed) 
    {
        if (newSpeed < 0)
            newSpeed = 0;
        if (newSpeed > 100)
            newSpeed = 100;
        List<string> result = exec(config.IPMIPath, $"-I {config.IPMIInterface}{config.IPMILogin} raw 0x30 0x30 0x02 0xff 0x{newSpeed.ToString("x2")}");
        try
        {
            PhysicalGPUHandle[] handles = GPUApi.EnumTCCPhysicalGPUs();
            foreach (PhysicalGPUHandle ph in handles)
            {
                try
                {
                    uint coolerID = GPUApi.GetClientFanCoolersControl(ph).FanCoolersControlEntries.FirstOrDefault().CoolerId;
                    GPUApi.SetClientFanCoolersControl(ph, new PrivateFanCoolersControlV1(new FanCoolersControlEntry[] { new PrivateFanCoolersControlV1.FanCoolersControlEntry(coolerID, NvAPIWrapper.Native.GPU.FanCoolersControlMode.Manual, Math.Min((uint)newSpeed*2, 100)) }));
                    //Console.WriteLine("Set card fan speed");
                    //Console.WriteLine(GPUApi.GetClientFanCoolersControl(ph).FanCoolersControlEntries[0].Level);
                }
                catch (Exception ex) { }
            }
        } catch (Exception ex) { Console.WriteLine(ex.Message); }
        currentFanSpeed = newSpeed;
        spinDown = DateTime.Now.AddSeconds(config.FanSpinDownDelay).Ticks;
        return $"Set fan speed to {newSpeed}%";
    }

    public static string ConnectToProgram(string title)
    {
        string name = title.Replace("Administrator:  ", "");
        if (!programs.ContainsKey(name))
        {
            Process[] processes = Process.GetProcessesByName("cmd"); // im launching batch files
            foreach (Process process in processes)
            {
                if (process.MainWindowTitle == title)
                {
                    programs.Add(name, config.Software[name]);
                    programs[name].Proc = process;
                    programs[name].State = ProcessState.Running;
                    return $"Application '{config.Software[name].Name}' connected.";
                }
            }
        }
        return $"Could not find {name}";
    }
    public static string StartSoftware(string name)
    {
        if (config.Software.ContainsKey(name))
        {
            if (!programs.ContainsKey(name))
            {
                programs.Add(name, config.Software[name]);
            }
            if (programs[name].State != ProcessState.Stopped)
            {
                return "Application is already " + Enum.GetName(programs[name].State);
            }
            programs[name].State = ProcessState.Starting;
            ProcessStartInfo procStart = new ProcessStartInfo(programs[name].Path, programs[name].Args);
            procStart.WorkingDirectory = Path.GetDirectoryName(programs[name].Path);
            procStart.UseShellExecute = true;
            programs[name].Proc = Process.Start(procStart);
            if (programs[name].Proc == null)
            {
                programs[name].State = ProcessState.Stopped;
                return "Application failed to start.";
            }
            programs[name].State = ProcessState.Running;
            return $"Application '{config.Software[name].Name}' started.";
        }
        return "Invalid program name.";
    }

    public static string StopSoftware(string name)
    {
        if (programs.ContainsKey(name))
        {
            if (programs[name].State == ProcessState.Stopping)
            {
                if (programs[name].Proc != null)
                {
                    programs[name].Proc.Kill();
                }
                programs[name].State = ProcessState.Stopped;
                return $"Terminating '{config.Software[name].Name}'";
            }
            if (programs[name].State != ProcessState.Running)
            {
                return "Application is already " + Enum.GetName(programs[name].State);
            }
            if (programs[name].Proc == null)
            {
                programs[name].State = ProcessState.Stopped;
                return "Application has no handle.";
            }
            programs[name].State = ProcessState.Stopping;
            if(programs[name].Proc.CloseMainWindow()) // like hitting the X on the window
                programs[name].State = ProcessState.Stopped;
            if(programs[name].Proc.HasExited)
                programs[name].State = ProcessState.Stopped;
            return $"Application '{config.Software[name].Name}' stopped.";
        }
        return "Invalid program name.";
    }

    public static async Task SendCommands()
    {
        List<ApplicationCommandProperties> applicationCommandProperties = new();
        List<ApplicationCommandProperties> guildCommandProperties = new();
        // I'm making them as global commands
        // owner - my server only?
        var fansCommand = new SlashCommandBuilder()
            .WithName("fans")
            .WithDescription("Control the server's fans manually")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("set")
                .WithDescription("Set the speed (%) of the server fans")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("speed", ApplicationCommandOptionType.Integer, "the fan speed to set as a %", isRequired: true))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("timeout")
                .WithDescription("Set a delay for the fans spin down")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("timeout", ApplicationCommandOptionType.String, "Enter a new delay as h:mm or mmm", isRequired: true))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("reset")
                .WithDescription("Reset the delay for the spin down")
                .WithType(ApplicationCommandOptionType.SubCommand));
        guildCommandProperties.Add(fansCommand.Build());

        // owner - my server only
        var regCommand = new SlashCommandBuilder()
            .WithName("reregister")
            .WithDescription("Rebuilds the discord slash command set from the current config");
        guildCommandProperties.Add(regCommand.Build());

        // admin - my server only?
        var progList = new SlashCommandOptionBuilder()
                    .WithName("name")
                    .WithType(ApplicationCommandOptionType.String)
                    .WithDescription("Target application")
                    .WithRequired(true);
        foreach (KeyValuePair<string, SoftwareRef> prog in config.Software)
        {
            progList.AddChoice(prog.Value.Name, prog.Key);
        }
        var programControlCommands = new SlashCommandBuilder()
            .WithName("software")
            .WithDescription("Allows starting and stopping of AI software on the compute units")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("start")
                .WithDescription("Starts the select piece of software")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(progList))
           .AddOption(new SlashCommandOptionBuilder()
                .WithName("stop")
                .WithDescription("Stops the select piece of software")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(progList));
        guildCommandProperties.Add(programControlCommands.Build());

        // admin
        var usersCommand = new SlashCommandBuilder()
            .WithName("user")
            .WithDescription("Adds or removes a user in the current server")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("action")
                .WithDescription("Determines if the user is to be added or removed")
                .WithType(ApplicationCommandOptionType.String)
                .WithRequired(true)
                .AddChoice("add", "add")
                .AddChoice("remove", "remove"))
            .AddOption("id", ApplicationCommandOptionType.User, "The user's ID", isRequired: true)
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("role")
                .WithDescription("Determines the role of the user")
                .WithType(ApplicationCommandOptionType.String)
                .WithRequired(true)
                .AddChoice("admin", "admin")
                .AddChoice("user", "user"));
        applicationCommandProperties.Add(usersCommand.Build());

        // user
        int commandLimit = 25;
        // everything is limited to the first 25
        foreach (KeyValuePair<string, Dictionary<string, ComfyUIField>> flow in config.ComfyUI.Flows)
        {
            var flowCommand = new SlashCommandBuilder()
                .WithName("draw_" + flow.Key)
                .WithDescription("Generate an image using this flow");
                //.WithType(ApplicationCommandOptionType.SubCommand)
            // add each field
            int fieldCount = 0;
            foreach (KeyValuePair<string, ComfyUIField> field in flow.Value)
            {
                //var parameterOption = new SlashCommandOptionBuilder()
                //    .WithName(field.Key)
                //    .WithDescription($"{field.Value.NodeTitle}: {field.Value.Field}")
                //    .WithType(ApplicationCommandOptionType.SubCommand);

                switch (field.Value.Type)
                {
                    case "List<checkpoint>":
                        {
                            List<string> models = ComfyMain.GetCheckpoints(config.ComfyUI.Paths.Checkpoints);
                            int count = 0;
                            var modelList = new SlashCommandOptionBuilder()
                                .WithName("checkpoint")
                                .WithType(ApplicationCommandOptionType.String)
                                .WithDescription("Model");
                            foreach (string m in models)
                            {
                                modelList.AddChoice(m, m);
                                count++;
                                if (count >= commandLimit)
                                    break;
                            }
                            modelList.IsRequired = field.Value.Required;
                            flowCommand.AddOption(modelList);
                        }
                        break;
                    case "List<lora>":
                        {
                            List<string> loras = ComfyMain.GetCheckpoints(config.ComfyUI.Paths.LoRAs + field.Value.Filter);
                            int count = 0;
                            var modelList = new SlashCommandOptionBuilder()
                                .WithName("lora")
                                .WithType(ApplicationCommandOptionType.String)
                                .WithDescription("Low-rank adaptation");
                            foreach (string l in loras)
                            {
                                modelList.AddChoice(l, field.Value.Filter + l);
                                count++;
                                if (count >= commandLimit)
                                    break;
                            }
                            modelList.IsRequired = field.Value.Required;
                            flowCommand.AddOption(modelList);
                        }
                        break;
                    case "Integer":
                    case "Boolean":
                    case "Number":
                    case "String":
                    case "Attachment":
                        {
                            flowCommand.AddOption(field.Key, Enum.Parse<ApplicationCommandOptionType>(field.Value.Type), field.Value.Field, isRequired: field.Value.Required);
                        }
                        break;
                    case "Random<Integer>":
                    case "Random<Number>":
                        {
                            string type = field.Value.Type.Substring(7, field.Value.Type.Length - 8);
                            flowCommand.AddOption(field.Key, Enum.Parse<ApplicationCommandOptionType>(type), field.Value.Field, isRequired: false);
                        }
                        break;
                    case "List<samplers>":
                    case "List<schedules>":
                    case "List<styles>":
                        {
                            string id = field.Value.Type.Substring(5, field.Value.Type.Length - 6);
                            if (!config.ComfyUI.Options.ContainsKey(id))
                                break;
                            int count = 0;
                            var optionList = new SlashCommandOptionBuilder()
                                .WithName(field.Key)
                                .WithType(ApplicationCommandOptionType.String)
                                .WithDescription($"{id} option");
                            foreach (string item in config.ComfyUI.Options[id])
                            {

                                optionList.AddChoice(item, item);
                                count++;
                                if (count >= commandLimit)
                                    break;
                            }
                            optionList.IsRequired = field.Value.Required;
                            flowCommand.AddOption(optionList);
                        }
                        break;
                }
                //flowCommand.AddOption(parameterOption);
                fieldCount++;
                if (fieldCount >= commandLimit)
                    break;
            }
            applicationCommandProperties.Add(flowCommand.Build());
            //comfyCommands.AddOption(flowCommand);
            //flowCount++;
            //if (flowCount >= commandLimit)
            //    break;
        }
        
        // user
        var statusCommand = new SlashCommandBuilder()
            .WithName("status")
            .WithDescription("Gets the full state of the server");
        applicationCommandProperties.Add(statusCommand.Build());

        try
        {
            if (discordInfo.OwnerServer != 0)
            {
                var guild = _client.GetGuild(discordInfo.OwnerServer);
                await guild.BulkOverwriteApplicationCommandAsync(guildCommandProperties.ToArray());
            }
            else
            {
                applicationCommandProperties = applicationCommandProperties.Concat(guildCommandProperties).ToList();
            }
            await _client.BulkOverwriteGlobalApplicationCommandsAsync(applicationCommandProperties.ToArray());
            //foreach (var acp in applicationCommandProperties)
            //    await _client.CreateGlobalApplicationCommandAsync(acp);
            // Using the ready event is a simple implementation for the sake of the example. Suitable for testing and development.
            // For a production bot, it is recommended to only run the CreateGlobalApplicationCommandAsync() once for each command.
        }
        catch (HttpException exception)
        {
            // If our command was invalid, we should catch an ApplicationCommandException. This exception contains the path of the error as well as the error message. You can serialize the Error field in the exception to get a visual of where your error is.
            var json = JsonConvert.SerializeObject(exception.Errors, Formatting.Indented);

            // You can send this error somewhere or just print it to the console, for this example we're just going to print it.
            Console.WriteLine(json);
        }
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

    public static async Task Client_Ready()
    {
        // do we need to do anything here?
    }

    private static async Task SlashCommandHandler(SocketSlashCommand command)
    {
        //Console.WriteLine(command.User.Id);
        if (command.GuildId.HasValue)
        {
            if (!discordInfo.Servers.ContainsKey(command.GuildId.Value))
            {
                SocketGuild guild = _client.GetGuild(command.GuildId.Value);
                discordInfo.Servers[command.GuildId.Value] = new ServerInfo();
                discordInfo.Servers[command.GuildId.Value].Name = guild.Name;
                Console.WriteLine($"Created new server {command.GuildId.Value} ({guild.Name})");
                Console.WriteLine(WriteUsers());
            }
        }
        AccessLevel level = discordInfo.CheckPermission(command.GuildId, command.User.Id);
        Console.WriteLine($"{command.User.GlobalName} ({command.User}) attempted to use {command.Data.Name} with permissions {Enum.GetName<AccessLevel>(level)}");
        if (level == AccessLevel.None)
        {
            await command.RespondAsync($"Command '{command.Data.Name}' not allowed.");
            return; // failed
        }
        if (command.Data.Name.StartsWith("draw_"))
        {
            string comfyFlow = command.Data.Name.Substring(5);
            if (config.ComfyUI.Flows.ContainsKey(comfyFlow))
            {
                Task.Run(async () =>
                {
                    string randomId = Guid.NewGuid().ToString("D");
                    await command.DeferAsync(false, RequestOptions.Default);
                    // get flow name
                    List<ComfyUIField> fields = new List<ComfyUIField>();
                    var flowName = comfyFlow;
                    foreach (var op in command.Data.Options)
                    {
                        if (config.ComfyUI.Flows[flowName][op.Name].Type == "Attachment" && op.Value.GetType() != typeof(string))
                        {
                            // upload to temp location
                            // replace with string. full path to image or partial?
                            // download
                            Attachment? attch = (op.Value as Attachment);
                            if (attch != null)
                            {
                                string imgPath = await ComfyMain.DownloadImage(attch.Url, $"{Program.config.ComfyUI.Paths.Temp}{randomId}_{attch.Filename}");
                                fields.Add(new ComfyUIField(config.ComfyUI.Flows[flowName][op.Name].NodeTitle, config.ComfyUI.Flows[flowName][op.Name].Field, imgPath, config.ComfyUI.Flows[flowName][op.Name].Object));
                            }
                            // dont add if there's a problem
                        }
                        else
                        {
                            fields.Add(new ComfyUIField(config.ComfyUI.Flows[flowName][op.Name].NodeTitle, config.ComfyUI.Flows[flowName][op.Name].Field, op.Value, config.ComfyUI.Flows[flowName][op.Name].Object));
                        }
                    }
                    await command.ModifyOriginalResponseAsync((s) => { s.Content = $"Request received.\n{GetDrawStatus()}"; });
                    HistoryResponse? res = await ComfyMain.EnqueueRequest(command.User.GlobalName, flowName, fields);
                    //await Log(new LogMessage(LogSeverity.Info, "ComfyUI", JsonConvert.SerializeObject(res)));
                    if (res == null || res.status.status_str != "success")
                    {
                        await command.ModifyOriginalResponseAsync((s) => { s.Content = "Your request has failed."; });
                    }
                    else
                    {
                        DiscordImageResponse response = CreateImageGenResponse(res, command.GuildId);
                        try
                        {
                            await command.ModifyOriginalResponseAsync((s) =>
                            {
                                s.Content = $"Here is your image {command.User.Mention}";
                                s.Attachments = response.Attachments;
                                s.Components = response.Components.Build();
                                s.AllowedMentions = new AllowedMentions(AllowedMentionTypes.Users);
                            });
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(ex.ToString());
                        }
                    }
                    // delete all temp images that start with randomId
                    //DirectoryInfo di = new DirectoryInfo(config.ComfyUI.Paths.Temp);
                    //foreach (FileInfo fi in di.GetFiles())
                    //{
                    //    if (fi.Name.StartsWith(randomId))
                    //        File.Delete(fi.FullName);
                    //}
                });
            }
            // draw command found
            return;
        }
        // user commands
        switch (command.Data.Name)
        {
            case "status":
                StringBuilder sb = new StringBuilder();
                sb.AppendLine($"Server status {DateTime.Now}:\n```CPU Temps: ");
                foreach (KeyValuePair<string, string> cpu in cpuTemps)
                {
                    sb.AppendLine($"\t{cpu.Key}\t{cpu.Value}");
                }
                sb.AppendLine($"\nGPU Statuses: ");
                sb.AppendLine($"\t{"Name".PadRight(16, ' ')} Temp\t Util\tFan%\tPerf");
                foreach (KeyValuePair<int, GPUStats> gpu in gpuPerf)
                {
                    sb.AppendLine($"\t{gpu.Value.Name.PadRight(16, ' ')} {gpu.Value.LastTemp.ToString().PadLeft(4)}\t{gpu.Value.LastUtilisation.ToString().PadLeft(4)}%\t{gpu.Value.FanSpeed.ToString().PadLeft(3)}%\t{(GPUPerfState == GPUPerformanceState.Auto ? Enum.GetName(gpu.Value.State) : Enum.GetName(GPUPerfState))}");
                    //sb.AppendLine($"\tGPU_{gpu.Key}\t{gpu.Value.LastTemp}\t{gpu.Value.LastUtilisation}%\t{(GPUPerfState == GPUPerformanceState.Auto ? Enum.GetName(gpu.Value.State) : Enum.GetName(GPUPerfState))}");
                }
                sb.AppendLine($"GPU Performance Mode: {Enum.GetName(GPUPerfState)}");
                sb.AppendLine($"\nFan Speed: ");
                foreach (KeyValuePair<string, string> fan in fanDetails)
                {
                    sb.AppendLine($"\t{fan.Key}\t{fan.Value}");
                }
                sb.AppendLine($"\nSoftware: ");
                sb.AppendLine($"\t{"Name".PadRight(12, ' ')}\t{"Status".PadRight(10)}\tQueue");
                foreach (KeyValuePair<string, SoftwareRef> prog in config.Software)
                {
                    sb.AppendLine($"\t{prog.Value.Name.PadRight(12, ' ')}\t{(programs.ContainsKey(prog.Key) ? Enum.GetName(programs[prog.Key].State) : "Unknown").PadRight(10)}\t{(prog.Value.Name == "ComfyUI" ? ComfyMain.CurrentQueueLength : 0)}");
                }
                sb.AppendLine("```");
                await command.RespondAsync(sb.ToString());
                return;

        }
        if (level < AccessLevel.Admin)
        {
            await command.RespondAsync($"Command '{command.Data.Name}' not allowed.");
            return; // failed
        }
        // admin commands
        switch (command.Data.Name)
        {
            case "user":
                {
                    string uaction = ""; // add or remove
                    ulong? server = command.GuildId; // required now?
                    IUser user = null;
                    string role = ""; // admin or user
                    foreach (var op in command.Data.Options)
                    {
                        switch (op.Name)
                        {
                            case "action":
                                {
                                    uaction = op.Value.ToString() ?? "";
                                    break;
                                }
                            case "id":
                                {
                                    user = (IUser)op.Value;
                                    break;
                                }
                            case "role":
                                {
                                    role = op.Value.ToString() ?? "";
                                    break;
                                }
                        }
                    }
                    if (user == null)
                        return;
                    switch (uaction)
                    {
                        case "add":
                            {
                                if (server.HasValue && server.Value != discordInfo.OwnerServer)
                                {
                                    if (discordInfo.Servers.ContainsKey(server.Value))
                                    {
                                        // server specific
                                        if (role == "user")
                                        {
                                            if (!discordInfo.Servers[server.Value].Users.Contains(user.Id))
                                                discordInfo.Servers[server.Value].Users.Add(user.Id);
                                        }
                                        else if (role == "admin")
                                        {
                                            if (!discordInfo.Servers[server.Value].Admins.Contains(user.Id))
                                                discordInfo.Servers[server.Value].Admins.Add(user.Id);
                                        }
                                        break; // we done
                                    }
                                }
                                if (role == "user")
                                {
                                    if (!discordInfo.GlobalUsers.Contains(user.Id))
                                        discordInfo.GlobalUsers.Add(user.Id);
                                }
                                else if (role == "admin")
                                {
                                    if (!discordInfo.GlobalAdmins.Contains(user.Id))
                                        discordInfo.GlobalAdmins.Add(user.Id);
                                }
                                break;
                            }
                        case "remove":
                            {
                                if (server.HasValue && server.Value != discordInfo.OwnerServer)
                                {
                                    if (discordInfo.Servers.ContainsKey(server.Value))
                                    {
                                        // server specific
                                        if (role == "user")
                                        {
                                            discordInfo.Servers[server.Value].Users.Remove(user.Id);
                                        }
                                        else if (role == "admin")
                                        {
                                            discordInfo.Servers[server.Value].Admins.Remove(user.Id);
                                        }
                                        break; // we done
                                    }
                                }
                                if (role == "user")
                                {
                                    discordInfo.GlobalUsers.Remove(user.Id);
                                }
                                else if (role == "admin")
                                {
                                    discordInfo.GlobalAdmins.Remove(user.Id);
                                }
                                break;
                            }
                    }
                    await command.RespondAsync(WriteUsers());
                    return;
                }
            case "software":
                var action = command.Data.Options?.First()?.Name;
                var name = command.Data.Options?.First().Options?.FirstOrDefault()?.Value;
                switch (action)
                {
                    case "start":
                        Task.Run(async () =>
                        {
                            if (name != null)
                            {
                                await command.DeferAsync(false, RequestOptions.Default);
                                string res = StartSoftware((string)name);
                                await Log(new LogMessage(LogSeverity.Info, "SlashCommandHandler", res));
                                await command.ModifyOriginalResponseAsync((s) => { s.Content = res; });
                            }
                        });
                        break;
                    case "stop":
                        Task.Run(async () =>
                        {
                            if (name != null)
                            {
                                await command.DeferAsync(false, RequestOptions.Default);
                                string res = StopSoftware((string)name);
                                await Log(new LogMessage(LogSeverity.Info, "SlashCommandHandler", res));
                                await command.ModifyOriginalResponseAsync((s) => { s.Content = res; });
                            }
                        });
                        break;
                }
                return;
        }
        if (level < AccessLevel.Owner)
        {
            await command.RespondAsync($"Command '{command.Data.Name}' not allowed.");
            return; // failed
        }
        // admin commands
        switch (command.Data.Name)
        {
            case "reregister":
                {
                    SendCommands();
                    await command.RespondAsync("Discord commands reregistered");
                }
                return;
            case "fans":
                {
                    string comName = command.Data.Options.First().Name;
                    switch (comName)
                    {
                        case "set":
                            var speed = command.Data.Options.First().Options?.FirstOrDefault()?.Value;
                            if (speed != null)
                            {
                                SetFanSpeed((int)(long)speed);
                            }
                            break;
                        case "timeout":
                            var time = command.Data.Options.First().Options?.FirstOrDefault()?.Value;
                            if (time != null)
                            {
                                SetTimeout((string)time);
                            }
                            break;
                        case "reset":
                            Reset();
                            break;
                    }
                    await command.RespondAsync($"Fans currently {(fanDetails.ContainsKey("Fan_%") ? fanDetails["Fan_%"] : "?")}% and will revert to {(fanDetails.ContainsKey("To__%") ? fanDetails["To__%"] : "?")}% @ {(fanDetails.ContainsKey("Until") ? fanDetails["Until"] : "?")}");
                    return;
                }
            default:
                await command.RespondAsync($"Command '{command.Data.Name}' not found.");
                return; // failed
        }
        
    }

    public static string GetDrawStatus()
    {
        return $"Queue length: {ComfyMain.CurrentQueueLength + 1}\nCurrent temp: {maxGPU}\nFan speed: {currentFanSpeed}";
    }
    // seems obnoxious, but we'll try get some re-use going
    public static DiscordImageResponse CreateImageGenResponse(HistoryResponse hr, ulong? server)
    {
        DiscordImageResponse dir = new DiscordImageResponse();
        try
        {
            List<ComfyUI.Image> images = new List<ComfyUI.Image>();
            foreach (var node in hr.outputs)
            {
                foreach (var results in node.Value)
                {
                    images = results.Value.Where(x => x.type == "output").ToList();
                }
            }
            List<FileAttachment> files = new List<FileAttachment>();
            ComponentBuilder cb = new ComponentBuilder();
            int i = 0;
            foreach (var img in images)
            {
                files.Add(new FileAttachment(config.ComfyUI.Paths.Outputs + img.subfolder + "/" + img.filename, img.filename, null, (bool)discordInfo.GetPreference(server, PreferenceNames.SpoilerImages) != false, false));
                if ((bool)(discordInfo.GetPreference(server, PreferenceNames.ShowUpscaleButton) ?? true) != false)
                {
                    ActionRowBuilder uarb = new ActionRowBuilder();
                    ButtonBuilder bbupscale125 = new ButtonBuilder($"1.25x", $"upscale:{hr.prompt[1].ToString()},{i},1.25", ButtonStyle.Primary, null, new Emoji("⤴️"), false, null);
                    ButtonBuilder bbupscale15 = new ButtonBuilder($"1.5x", $"upscale:{hr.prompt[1].ToString()},{i},1.5", ButtonStyle.Primary, null, new Emoji("⤴️"), false, null);
                    ButtonBuilder bbupscale20 = new ButtonBuilder($"2x", $"upscale:{hr.prompt[1].ToString()},{i},2.0", ButtonStyle.Primary, null, new Emoji("⤴️"), false, null);
                    ButtonBuilder bbupscale30 = new ButtonBuilder($"3x", $"upscale:{hr.prompt[1].ToString()},{i},3.0", ButtonStyle.Primary, null, new Emoji("⤴️"), false, null);

                    uarb.AddComponent(bbupscale125.Build());
                    uarb.AddComponent(bbupscale15.Build());
                    uarb.AddComponent(bbupscale20.Build());
                    uarb.AddComponent(bbupscale30.Build());
                    cb.AddRow(uarb);
                }
                if ((bool)(discordInfo.GetPreference(server, PreferenceNames.ShowVariationMenu) ?? true) != false)
                {
                    ActionRowBuilder varb = new ActionRowBuilder();
                    List<SelectMenuOptionBuilder> lsmob = new List<SelectMenuOptionBuilder>();
                    lsmob.Add(new SelectMenuOptionBuilder("40%", "0.4"));
                    lsmob.Add(new SelectMenuOptionBuilder("60%", "0.6"));
                    lsmob.Add(new SelectMenuOptionBuilder("75%", "0.75"));
                    lsmob.Add(new SelectMenuOptionBuilder("85%", "0.85"));
                    lsmob.Add(new SelectMenuOptionBuilder("90%", "0.9"));
                    lsmob.Add(new SelectMenuOptionBuilder("95%", "0.95"));
                    SelectMenuBuilder vsmb = new SelectMenuBuilder($"variation:{hr.prompt[1].ToString()}", lsmob, "Variation");
                    varb.AddComponent(vsmb.Build());
                    cb.AddRow(varb);
                }
                //ButtonBuilder bb = new ButtonBuilder("v1", res.prompt[1].ToString(), ButtonStyle.Secondary, null, Emote.Parse("<:arrow_heading_up:>"), false, null);
                if ((bool)(discordInfo.GetPreference(server, PreferenceNames.ShowRegenerateButton) ?? true) != false)
                {
                    ActionRowBuilder arb = new ActionRowBuilder();
                    ButtonBuilder bbredo = new ButtonBuilder($"Reroll", $"regenerate:{hr.prompt[1].ToString()}", ButtonStyle.Success, null, new Emoji("🎲"), false, null);
                    arb.AddComponent(bbredo.Build());
                    cb.AddRow(arb);
                }
            }
            dir.Attachments = files;
            dir.Components = cb;
            return dir;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.ToString());
            return dir;
        }
    }
}

