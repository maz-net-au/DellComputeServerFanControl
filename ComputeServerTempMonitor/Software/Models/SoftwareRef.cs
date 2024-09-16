using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor.Software.Models
{
    public enum ProcessState
    {
        Stopped,
        Starting,
        Running,
        Stopping
    }
    public class SoftwareRef
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public string Args { get; set; } = "";
        public Process? Proc { get; set; }
        // is there a way to know if it's started?
        public ProcessState State { get; set; } = ProcessState.Stopped;
        // going to eventually want to make some kind of IPC layer in here?
        // api over to it?
    }
}
