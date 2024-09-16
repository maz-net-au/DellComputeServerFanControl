using ComputeServerTempMonitor.ComfyUI;
using System.Diagnostics;
using ComputeServerTempMonitor.Common;
using ComputeServerTempMonitor.Software;
using ComputeServerTempMonitor.Hardware;
using ComputeServerTempMonitor.Discord;

namespace ComputeServerTempMonitor;

// add support for controlling the gpu fans, not just the chassis fans (per gpu, but combined for chassis?)
// llama.cpp support for my discord bot -> stand-alone exe that the bot talks to?
// Or just investigate the oobabooga API
//  - confirm if CUDA_VISIBLE_DEVICES works with llama sharp
//  - A slash command to start a new conversation. takes a system prompt as input?
//  - Replies come back in a thread. posts in that thread are automatically added as part of the conversation
//  - A button to re-generate the last response?
//  - a command or button that pops up a modal to replace the last bot response with something else
//  - a continue button? would I be better off just limiting responses to 200 chars and making continue work?
//  - check if responses can be edited by the bot (so i can send partial replies and update them)
// see if i can calculate how long comfyui will take to finish it's queue / each request
// see if i can extend the message timeout from the bot or close off the old message and just post a new one?
// check out making modals and image editor popups
// see if progress % or queue length can be constantly updated somewhere or ETA
// queue work items / requests on my side and only send them when a slot is free. priority users have their own queue?
// if I can find a way to get the original flow name (store it in a meta field in comfyUI history object somehow?) then i can reroll / variation from history instead of request cache
// log data as a CSV for server maintenance purposes. good to know temp over time for a year or something
// nest flows in the config so that they can be disabled
// allow user level permissions for flows
// add vram usage to status output

class Program
{
    static bool IsRunning = true;
    const string configFile = "data/config.json";
    static CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();

    static void CurrentDomain_ProcessExit(object sender, EventArgs e)
    {
        Exit();
    }

    static void Exit()
    {
        // call an exit function for each one
        IsRunning = false;
        cancellationTokenSource.Cancel();
        ComfyMain.Exit();
        DiscordMain.Exit();
        HardwareMain.Exit();
        SoftwareMain.Exit();
    }

    static async Task Main(string[] args)
    {
        AppDomain.CurrentDomain.ProcessExit += new EventHandler(CurrentDomain_ProcessExit);
        
        // today we're not wanting to test discord bot features. just get the users set up
        if (!System.Diagnostics.Debugger.IsAttached)
        {
            
        }
        // is order important?
        try
        {
            SharedContext.Instance.LoadConfig(configFile); 

            ComfyMain.Init(cancellationTokenSource.Token);
            DiscordMain.Init(cancellationTokenSource.Token);
            HardwareMain.Init(cancellationTokenSource.Token);
            SoftwareMain.Init(cancellationTokenSource.Token);
        }
        catch (Exception ex)
        {
            SharedContext.Instance.Log(LogLevel.ERR, "Main", ex.ToString());
        }
        //OobaboogaMain.Init(cancellationTokenSource.Token);
        //object test = new Dictionary<string, string>();
        //await OobaboogaMain.RunArbitraryCommand(test);
        //return;

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
                    string mode = key.KeyChar.ToString().ToLower();
                    switch (mode)
                    {
                        case "d":
                            SharedContext.Instance.Log(LogLevel.INFO, "Main", "Dispaying current state. Press any key to cancel.");
                            int headerRow = 0;
                            while (!Console.KeyAvailable)
                            {
                                Console.WriteLine(HardwareMain.GetCLIStatus(headerRow % 25 == 0));
                                headerRow++;
                                Thread.Sleep(5000);
                            }
                            Console.ReadKey(true); // discard the key we got
                            lastKeyDefault = false;
                            break;
                        case "s":
                            SharedContext.Instance.Log(LogLevel.INFO, "Main", "Setting a new fan speed. Enter a number between 0 and 100");
                            string rIn = Console.ReadLine() ?? "";
                            uint newSpeed = 0;
                            if (uint.TryParse(rIn, out newSpeed))
                            {
                                SharedContext.Instance.Log(LogLevel.INFO, "Main", HardwareMain.SetChassisFanSpeed(newSpeed));
                            }
                            else
                            {
                                SharedContext.Instance.Log(LogLevel.INFO, "Main", $"Invalid speed entered: '{rIn}'");
                            }
                            lastKeyDefault = false;
                            break;
                        case "r":
                            SharedContext.Instance.Log(LogLevel.INFO, "Main", HardwareMain.Reset());
                            lastKeyDefault = false;
                            break;
                        case "t":
                            SharedContext.Instance.Log(LogLevel.INFO, "Main", "Enter a new delay as h:mm or mmm.");
                            string time = Console.ReadLine() ?? "";
                            SharedContext.Instance.Log(LogLevel.INFO, "Main", HardwareMain.SetTimeout(time));
                            lastKeyDefault = false;
                            break;
                        case "c":
                            SharedContext.Instance.LoadConfig(configFile);
                            SharedContext.Instance.Log(LogLevel.INFO, "Main", "Config loaded");
                            lastKeyDefault = false;
                            break;
                        case "u":
                            DiscordMain.LoadUsers();
                            SharedContext.Instance.Log(LogLevel.INFO, "Main", "Users file re-loaded");
                            lastKeyDefault = false;
                            break;
                        //case "i":
                        //    SharedContext.Instance.Log(LogLevel.INFO, "Main", GPUSleepAll()));
                        //    lastKeyDefault = false;
                        //    break;
                        //case "w":
                        //    SharedContext.Instance.Log(LogLevel.INFO, "Main", GPUWakeAll()));
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
                        //    SharedContext.Instance.Log(LogLevel.INFO, "Main", "Setting all GPUs to auto power state"));
                        //    lastKeyDefault = false;
                        //    break;
                        case "x":
                            SharedContext.Instance.Log(LogLevel.INFO, "Main", "Program exiting");
                            Exit();
                            lastKeyDefault = false;
                            break;
                        case "z":
                            await DiscordMain.SendCommands();
                            SharedContext.Instance.Log(LogLevel.INFO, "Main", "Discord commands reregistered");
                            lastKeyDefault = false;
                            break;
                        case "h":
                                SharedContext.Instance.Log(LogLevel.INFO, "Main", "'d' to dispay current state. press any key to stop");
                                SharedContext.Instance.Log(LogLevel.INFO, "Main", "'s' to set a new fan speed.");
                                SharedContext.Instance.Log(LogLevel.INFO, "Main", "'r' to reset, and ignore the cooldown timer.");
                                SharedContext.Instance.Log(LogLevel.INFO, "Main", "'c' to reload the configuration file.");
                                SharedContext.Instance.Log(LogLevel.INFO, "Main", "'u' to reload the users file.");
                                SharedContext.Instance.Log(LogLevel.INFO, "Main", "'t' to set a new cooldown delay.");
                                SharedContext.Instance.Log(LogLevel.INFO, "Main", "'z' to reregister all discord commands");
                                SharedContext.Instance.Log(LogLevel.INFO, "Main", "'x' to exit");
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
                SharedContext.Instance.Log(LogLevel.ERR, "Main", "In input loop: " + ex.Message);
            }
        }
        cancellationTokenSource.Cancel();
    }
}

