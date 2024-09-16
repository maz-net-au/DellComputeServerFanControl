using ComputeServerTempMonitor.ComfyUI;
using ComputeServerTempMonitor.Common;
using ComputeServerTempMonitor.Software.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor.Software
{
    public static class SoftwareMain
    {
        static Dictionary<string, SoftwareRef> programs = new Dictionary<string, SoftwareRef>();
        static CancellationToken cancellationToken;

        public static void Init(CancellationToken ct)
        {
            cancellationToken = ct;
            foreach (KeyValuePair<string, SoftwareRef> p in SharedContext.Instance.GetConfig().Software)
            {
                SharedContext.Instance.Log(LogLevel.INFO, "Startup", SoftwareMain.ConnectToProgram("Administrator:  " + p.Key));
            }
        }

        public static string GetStatus()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"Software: ");
            // queue should probably live elsewhere. in the individual status response for ComfyMain and OobaboogaMain
            sb.AppendLine($"\t{"Name".PadRight(12, ' ')}\t{"Status".PadRight(10)}\tQueue");
            foreach (KeyValuePair<string, SoftwareRef> prog in SharedContext.Instance.GetConfig().Software)
            {
                sb.AppendLine($"\t{prog.Value.Name.PadRight(12, ' ')}\t{(programs.ContainsKey(prog.Key) ? Enum.GetName(programs[prog.Key].State) : "Unknown").PadRight(10)}\t{(prog.Value.Name == "ComfyUI" ? ComfyMain.CurrentQueueLength : 0)}");
            }
            return sb.ToString();
        }

        public static string ConnectToProgram(string title)
        {
            string name = title.Replace("Administrator:  ", "");
            if (!programs.ContainsKey(name))
            {
                Process[] processes = Process.GetProcessesByName("cmd"); // im launching batch files
                // there's an issue where using the settings menu in comfyUI to restart the server changes the title.. for some reason
                foreach (Process process in processes)
                {
                    if (process.MainWindowTitle == title)
                    {
                        programs.Add(name, SharedContext.Instance.GetConfig().Software[name]);
                        programs[name].Proc = process;
                        programs[name].State = ProcessState.Running;
                        return $"Application '{SharedContext.Instance.GetConfig().Software[name].Name}' connected.";
                    }
                }
            }
            return $"Could not find {name}";
        }
        public static string StartSoftware(string name)
        {
            if (SharedContext.Instance.GetConfig().Software.ContainsKey(name))
            {
                if (!programs.ContainsKey(name))
                {
                    programs.Add(name, SharedContext.Instance.GetConfig().Software[name]);
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
                return $"Application '{SharedContext.Instance.GetConfig().Software[name].Name}' started.";
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
                    return $"Terminating '{SharedContext.Instance.GetConfig().Software[name].Name}'";
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
                if (programs[name].Proc.CloseMainWindow()) // like hitting the X on the window
                    programs[name].State = ProcessState.Stopped;
                if (programs[name].Proc.HasExited)
                    programs[name].State = ProcessState.Stopped;
                return $"Application '{SharedContext.Instance.GetConfig().Software[name].Name}' stopped.";
            }
            return "Invalid program name.";
        }

        public static void Exit()
        {
            // do nothing. we leave it running here
        }
    }
}
