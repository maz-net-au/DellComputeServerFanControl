using Discord;
using Discord.Commands;
using Discord.Interactions;
using Discord.Net;
using Discord.WebSocket;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NvAPIWrapper.Native;
using NvAPIWrapper.Native.GPU.Structures;
using NvAPIWrapper.Native.Interfaces.GPU;
using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices.Marshalling;
using System.Text;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace ComputeServerTempMonitor;

// TODO:
// idle detection (based on power usage? i still want to try and run things cooler)
// 3d print a guide for air through the T4's
// discord bot?
// move the map of software to paths and args into config
// generate the slash commands dynamically

class Program
{
    private static DiscordSocketClient _client;
    private readonly CommandService _commands;
    static int maxCPU = 0;
    static int maxGPU = 0;
    static int currentFanSpeed = 0;
    static long spinDown = 0;
    static bool IsRunning = true;
    static bool ForcedGPUIdle = false;

    //static Dictionary<string, string> currentStats = new Dictionary<string, string>();
    static Dictionary<string, string> cpuTemps = new Dictionary<string, string>();
    static Dictionary<string, string> gpuTemps = new Dictionary<string, string>();
    static Dictionary<string, string> fanDetails = new Dictionary<string, string>();
    static Dictionary<string, SoftwareRef> programs = new Dictionary<string, SoftwareRef>();

    // read config for rules
    static Config config;

    //static void Log(string message)
    //{
    //    Console.WriteLine(message);
    //}

    private static Task Log(LogMessage msg)
    {
        Console.WriteLine(msg.ToString());
        return Task.CompletedTask;
    }

    static async Task Main(string[] args)
    {
        string newMode = "x";
        string mode = ""; // default to displaying temps

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
        
        if (config.IPMIPath == "")
        {
            await Log(new LogMessage(LogSeverity.Info, "Main", "No path set to IPMItool. Cannot proceed."));
            await Log(new LogMessage(LogSeverity.Info, "Main", "Press any key to exit"));
            Console.ReadKey();
            return;
        }

        _client = new DiscordSocketClient();
        _client.Log += Log;
        _client.Ready += Client_Ready;
        _client.SlashCommandExecuted += SlashCommandHandler;

        await _client.LoginAsync(TokenType.Bot, config.DiscordBotToken);
        await _client.StartAsync();
        //await Task.Delay(-1);

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
                Console.WriteLine(headers);
            }
            Console.WriteLine(values);
        };

        // on startup, call IPMI commands to disable PCIe card response
        List<string> result = exec(config.IPMIPath, $"-I {config.IPMIInterface}{config.IPMILogin} raw 0x30 0xce 0x00 0x16 0x05 0x00 0x00 0x00 0x05 0x00 0x00 0x00 0x00");
        // set fan manual mode
        result = exec(config.IPMIPath, $"-I {config.IPMIInterface}{config.IPMILogin} raw 0x30 0x30 0x01 0x00");
        // set default rate
        result = exec(config.IPMIPath, $"-I {config.IPMIInterface}{config.IPMILogin} raw 0x30 0x30 0x02 0xff 0x{config.DefaultFanSpeed.ToString("x2")}");
        currentFanSpeed = config.DefaultFanSpeed;
        await Log(new LogMessage(LogSeverity.Info, "Main", "Controlling fans"));
        // in a task
        if (config.SMIPath != "")
        {

            Task tGpu = new Task(async () =>
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
                        result = exec(config.IPMIPath, $"-I {config.IPMIInterface}{config.IPMILogin} raw 0x30 0x30 0x02 0xff 0x{newSpeed.ToString("x2")}");
                        currentFanSpeed = newSpeed;
                        printState(true);
                    }
                    fanDetails["Until"] = new DateTime(spinDown).ToString();
                }
                catch (Exception ex)
                {
                    await Log(new LogMessage(LogSeverity.Error, "Main", "Error processing fan speeds: " + ex.Message));
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
                            await Log(new LogMessage(LogSeverity.Info, "Main", "Dispaying current state. Press any key to cancel."));
                            int headerRow = 0;
                            while (!Console.KeyAvailable)
                            {
                                printState(headerRow % 25 == 0);
                                headerRow++;
                                Thread.Sleep(1000);
                            }
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
                            break;
                        case "r":
                            await Log(new LogMessage(LogSeverity.Info, "Main", Reset()));
                            break;
                        case "t":
                            await Log(new LogMessage(LogSeverity.Info, "Main", "Enter a new delay as h:mm or mmm."));
                            string time = Console.ReadLine() ?? "";
                            await Log(new LogMessage(LogSeverity.Info, "Main", SetTimeout(time)));
                            break;
                        case "c":
                            await Log(new LogMessage(LogSeverity.Info, "Main", LoadConfig()));
                            break;
                        case "i":
                            await Log(new LogMessage(LogSeverity.Info, "Main", GPUSleep()));
                            break;
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
                        //        await Log(new LogMessage(LogSeverity.Info, "Main", ex.ToString()));
                        //    }
                        //    break;
                        case "w":
                            await Log(new LogMessage(LogSeverity.Info, "Main", GPUWake()));
                            break;
                        case "x":
                            await Log(new LogMessage(LogSeverity.Info, "Main", Exit()));
                            break;
                        default:
                            await Log(new LogMessage(LogSeverity.Info, "Main", "'d' to dispay current state. press any key to stop"));
                            await Log(new LogMessage(LogSeverity.Info, "Main", "'s' to set a new fan speed."));
                            await Log(new LogMessage(LogSeverity.Info, "Main", "'r' to reset, and ignore the cooldown timer."));
                            await Log(new LogMessage(LogSeverity.Info, "Main", "'c' to reload the configuration file."));
                            await Log(new LogMessage(LogSeverity.Info, "Main", "'t' to set a new cooldown delay."));
                            await Log(new LogMessage(LogSeverity.Info, "Main", "'i' to force the GPUs into an idle state."));
                            await Log(new LogMessage(LogSeverity.Info, "Main", "'w' to restores the GPU high power states."));
                            await Log(new LogMessage(LogSeverity.Info, "Main", "'x' to exit"));
                            break;
                    }
                    mode = "";
                }
            }
            catch (Exception ex)
            {
                await Log(new LogMessage(LogSeverity.Info, "Main", "In input loop: " + ex.Message, ex));
            }
        }
        cancellationTokenSource.Cancel();
    }

    public static string Exit()
    {
        // quit any running processes?
        IsRunning = false;
        // restore the fan states so that nothing catches fire
        exec(config.IPMIPath, $"-I {config.IPMIInterface}{config.IPMILogin} raw 0x30 0x30 0x01 0x01");
        exec(config.IPMIPath, $"-I {config.IPMIInterface}{config.IPMILogin} raw 0x30 0xce 0x00 0x16 0x05 0x00 0x00 0x00 0x05 0x00 0x01 0x00 0x00");
        return "Disabled manual control and restored PCIe cooling response.";
    }

    public static string Reset() 
    {
        spinDown = DateTime.Now.Ticks;
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

    public static string GPUSleep()
    {
        try
        {
            PhysicalGPUHandle[] handles = GPUApi.EnumTCCPhysicalGPUs();
            foreach (PhysicalGPUHandle ph in handles)
            {
                GPUApi.SetForcePstate(ph, 8, 2);
            }
            ForcedGPUIdle = true;
            return "Setting all GPUs to a low power state";
        }
        catch (Exception ex)
        {
            return $"Error when setting GPUs into a low power state: {ex.Message}";
        }
    }

    public static string GPUWake()
    {
        try
        {
            PhysicalGPUHandle[] handles = GPUApi.EnumTCCPhysicalGPUs();
            foreach (PhysicalGPUHandle ph in handles)
            {
                GPUApi.SetForcePstate(ph, 16, 2);
            }
            ForcedGPUIdle = false;
            return "Restoring all GPUs to the default power state";
        }
        catch (Exception ex)
        {
            return $"Error when restoring all GPUs to the default power state: {ex.Message}";
        }
    }

    public static string SetFanSpeed(int newSpeed) 
    {
        if (newSpeed < 0)
            newSpeed = 0;
        if (newSpeed > 100)
            newSpeed = 100;
        List<string> result = exec(config.IPMIPath, $"-I {config.IPMIInterface}{config.IPMILogin} raw 0x30 0x30 0x02 0xff 0x{newSpeed.ToString("x2")}");
        currentFanSpeed = newSpeed;
        spinDown = DateTime.Now.AddSeconds(config.FanSpinDownDelay).Ticks;
        return $"Set fan speed to {newSpeed}%";
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
        List<ApplicationCommandProperties> applicationCommandProperties = new();
        // I'm making them as global commands
        var fansCommand = new SlashCommandBuilder()
            .WithName("fans")
            .WithDescription("Control the server's fans manually")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("info")
                .WithDescription("Get the current state of the server fans")
                .WithType(ApplicationCommandOptionType.SubCommand))
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
        applicationCommandProperties.Add(fansCommand.Build());

        var gpuCommand = new SlashCommandBuilder()
            .WithName("gpu")
            .WithDescription("Controls advanced settings for the Tesla compute units")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("idle")
                .WithDescription("Force all compute units into a low power state")
                .WithType(ApplicationCommandOptionType.SubCommand))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("wake")
                .WithDescription("Allow all compute units to access high power states")
                .WithType(ApplicationCommandOptionType.SubCommand));
        applicationCommandProperties.Add(gpuCommand.Build());

        var progList = new SlashCommandOptionBuilder()
                    .WithName("name")
                    .WithType(ApplicationCommandOptionType.String)
                    .WithDescription("Target application")
                    .WithRequired(true);
        foreach (KeyValuePair<string, SoftwareRef> prog in config.Software)
        {
            progList.AddChoice(prog.Value.Name, prog.Key);
        }
        //.AddChoice("LLM", "llm")
        //.AddChoice("ComfyUI1", "image-gen-1")
        //.AddChoice("ComfyUI2", "image-gen-2")
        //.AddChoice("Bot", "image-gen-bot");

        var programControlCommands = new SlashCommandBuilder()
            .WithName("software")
            .WithDescription("Allows starting and stopping of AI software on the compute units")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("status")
                .WithDescription("Get the current status of software tasks")
                .WithType(ApplicationCommandOptionType.SubCommand))
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
        applicationCommandProperties.Add(programControlCommands.Build());

        var statusCommand = new SlashCommandBuilder()
            .WithName("status")
            .WithDescription("Gets the full state of the server");
        applicationCommandProperties.Add(statusCommand.Build());

        try
        {
            await _client.BulkOverwriteGlobalApplicationCommandsAsync(applicationCommandProperties.ToArray());
            // Using the ready event is a simple implementation for the sake of the example. Suitable for testing and development.
            // For a production bot, it is recommended to only run the CreateGlobalApplicationCommandAsync() once for each command.
        }
        catch (ApplicationCommandException exception)
        {
            // If our command was invalid, we should catch an ApplicationCommandException. This exception contains the path of the error as well as the error message. You can serialize the Error field in the exception to get a visual of where your error is.
            var json = JsonConvert.SerializeObject(exception.Errors, Formatting.Indented);

            // You can send this error somewhere or just print it to the console, for this example we're just going to print it.
            Console.WriteLine(json);
        }
    }

    private static async Task SlashCommandHandler(SocketSlashCommand command)
    {
        switch (command.Data.Name)
        {
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
                            await command.RespondAsync(Reset());
                            break;
                    }
                    await command.RespondAsync($"Fans set to {(fanDetails.ContainsKey("Fan_%") ? fanDetails["Fan_%"] : "?")}% and will revert to {(fanDetails.ContainsKey("To__%") ? fanDetails["To__%"] : "?")}% @ {(fanDetails.ContainsKey("Until") ? fanDetails["Until"] : "?")}");
                    break;
                }
            case "gpu":
                {
                    string comName = command.Data.Options.First().Name;
                    switch (comName)
                    {
                        case "idle":
                            await command.RespondAsync(GPUSleep());
                            break;
                        case "wake":
                            await command.RespondAsync(GPUWake());
                            break;
                    }
                    break;
                }
            case "software":
                var action = command.Data.Options?.First()?.Name;
                var name = command.Data.Options?.First().Options?.FirstOrDefault()?.Value;
                switch (action)
                {
                    case "start":
                        if (name != null)
                        {
                            await command.DeferAsync(false, RequestOptions.Default);
                            string res = StartSoftware((string)name);
                            await Log(new LogMessage(LogSeverity.Info, "SlashCommandHandler", res));
                            await command.ModifyOriginalResponseAsync((s) => { s.Content = res; });
                        }
                        break;
                    case "stop":
                        if (name != null)
                        {
                            await command.DeferAsync(false, RequestOptions.Default);
                            string res = StopSoftware((string)name);
                            await Log(new LogMessage(LogSeverity.Info, "SlashCommandHandler", res));
                            await command.ModifyOriginalResponseAsync((s) => { s.Content = res; });
                        }
                        break;
                    case "status":
                        string result = "";
                        foreach (KeyValuePair<string, SoftwareRef> prog in config.Software)
                        {
                            result += $"\n{prog.Value.Name} : {(programs.ContainsKey(prog.Key) ? Enum.GetName(programs[prog.Key].State) : "Unknown")}";
                        }
                        await command.RespondAsync(result);
                        break;
                }                
                break;

            case "status":
                StringBuilder sb = new StringBuilder();
                sb.AppendLine($"Server status {DateTime.Now}:\n```CPU Temps: ");
                foreach (KeyValuePair<string, string> cpu in cpuTemps)
                {
                    sb.AppendLine($"\t{cpu.Key}\t{cpu.Value}");
                }
                sb.AppendLine($"\nGPU Temps: ");
                foreach (KeyValuePair<string, string> gpu in gpuTemps)
                {
                    sb.AppendLine($"\t{gpu.Key}\t{gpu.Value}");
                }
                sb.AppendLine($"GPU Performance State: {(ForcedGPUIdle ? "P8" : "P0")}");
                sb.AppendLine($"\nFan Speed: ");
                foreach (KeyValuePair<string, string> fan in fanDetails)
                {
                    sb.AppendLine($"\t{fan.Key}\t{fan.Value}");
                }
                sb.AppendLine($"\nSoftware: ");
                foreach (KeyValuePair<string, SoftwareRef> prog in config.Software)
                {
                    sb.AppendLine($"\t{prog.Value.Name.PadRight(12, ' ')}\t{(programs.ContainsKey(prog.Key) ? Enum.GetName(programs[prog.Key].State) : "Unknown")}");
                }
                sb.AppendLine("```");
                await command.RespondAsync(sb.ToString());
                break;
        }
        
    }
}


// 