using ComputeServerTempMonitor.Common;
using ComputeServerTempMonitor.Software.Models;
using ComputeServerTempMonitor.Software;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor.IoT
{
    public static class IoTMain
    {
        public static HttpClient hc = new HttpClient();
        static CancellationToken cancellationToken;

        public static void Init(CancellationToken ct)
        {
            cancellationToken = ct;
        }

        public static void Exit() { }
        public static async Task<string> GetCameraFrame(string name)
        {
            if (!Directory.Exists("temp/iot"))
            {
                Directory.CreateDirectory("temp/iot");
            }
            // get frame
            HttpResponseMessage response = await hc.GetAsync($"{SharedContext.Instance.GetConfig().mIoT.URL}/api/device/camera/{name}/frame");
            byte[] frame = await response.Content.ReadAsByteArrayAsync();
            if (frame.Length > 1000)
            {
                // store it in temp
                string filename = $"temp/iot/{name}_{DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond}-{new Random().Next(999999):000000}.jpg";
                File.WriteAllBytes(filename, frame);
                // return path
                return filename;
            }
            return "";            
        }

        // how do we interact with other things. what have we got? ac, lights, ir, leds, cat door and feeder, fans
        // my hub needs to know what features are used on each device. is that a many -> many?
        // then i want to list devices by group
        // then i want to be able to interact with parts. this might be relatively hard-coded
    }
}
