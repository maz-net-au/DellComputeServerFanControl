using ComputeServerTempMonitor.Common;
using ImageMagick;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor.Anthropic.Models
{
    public class ClaudeContentSource
    {
        public ClaudeContentSource() { }
        public ClaudeContentSource(string path) 
        {
            // check type
            string ext = Path.GetExtension(path).ToLower().Trim('.');
            switch (ext)
            {
                // set media_type
                case "jpg":
                case "jpeg":
                    media_type = "image/jpeg";
                    break;
                case "png":
                    media_type = "image/png";
                    break;
                case "webp":
                    media_type = "image/webp";
                    break;
                case "pdf":
                    media_type = "application/pdf";
                    break;
                default:
                    media_type = "unknown/" + ext; 
                    break;
            }
            // encode
            if (media_type.StartsWith("image/"))
                data = Convert.ToBase64String(ResizeImage(path));
            else
                data = Convert.ToBase64String(File.ReadAllBytes(path));
        }
        public string type { get; set; } = "base64";
        public string media_type { get; set; } = "image/jpeg";
        public string data { get; set; }

        private byte[] ResizeImage(string path)
        {
            uint target = 1150000;
            SharedContext.Instance.Log(LogLevel.INFO, "Claude", "Received image");
            using MagickImage image = new MagickImage(File.ReadAllBytes(path));
            uint pixCount = image.Height * image.Width;
            if (pixCount > target)
            {
                //SharedContext.Instance.Log(LogLevel.INFO, "Claude", $"Resizing image: {image.Height}x{image.Width}={pixCount}");
                double ratio = (double)image.Height / (double)image.Width;
                double newX = Math.Sqrt(target / ratio);
                double newY = ratio * newX;
                //SharedContext.Instance.Log(LogLevel.INFO, "Claude", $"WTF: {ratio} {newX} {newY}");
                if (newX > 1568 || newY > 1568)
                {
                    var size = new MagickGeometry(1568, 1568); // claude api docs say this is the max it'll accept. they'll probs scale it down still
                    image.Resize(size);
                    //SharedContext.Instance.Log(LogLevel.INFO, "Claude", $"Resized based on max size");
                }
                else
                {
                    image.Resize((uint)newX, (uint)newY);
                    //SharedContext.Instance.Log(LogLevel.INFO, "Claude", $"new image: {newY}x{newX}={newX * newY}");
                }
                string newName = Path.GetDirectoryName(path) + "\\" + Path.GetFileNameWithoutExtension(path) + "_rsz" + Path.GetExtension(path);
                SharedContext.Instance.Log(LogLevel.INFO, "Claude", $"Saving resized image: {newName} from {image.Height}x{image.Width}={pixCount} to {(uint)newY}x{(uint)newX}={(uint)newX * (uint)newY}");
                image.Write(newName, image.Format);
                return File.ReadAllBytes(newName);
            }
            else
            {
                return File.ReadAllBytes(path);
            }
        }
    }
}
