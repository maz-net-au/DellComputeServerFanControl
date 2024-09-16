using Discord;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor.Discord.Models
{
    public class DiscordImageResponse
    {
        public DiscordImageResponse() { }
        public List<FileAttachment> Attachments { get; set; }
        public ComponentBuilder Components { get; set; }
        public Dictionary<ImageGenStatisticType, string> Statistics { get; set; } = new Dictionary<ImageGenStatisticType, string>();
    }

    public enum ImageGenStatisticType
    {
        Unknown = 0x00,
        Id = 0x01,
        Height = 0x02,
        Width = 0x03,
        FileSize = 0x04,
        FileName = 0x05,
    }
}
