using Discord;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor
{
    public class DiscordImageResponse
    {
        public DiscordImageResponse() { }
        public List<FileAttachment> Attachments { get; set; }
        public ComponentBuilder Components { get; set; }
    }
}
