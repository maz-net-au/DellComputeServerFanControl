using ComputeServerTempMonitor.Common;
using Discord.Audio;
using System.Diagnostics;

namespace ComputeServerTempMonitor.Discord
{
    public class InputStream
    {
        public ulong UserId { get; set; }
        public AudioInStream UserStream { get; set; }
        public bool IsListening { get; set; } = true;
        public CancellationTokenSource CancellerSource { get; set; }
        public Task Listener { get; set; }

        public InputStream(ulong userId, AudioInStream stream)
        {
            UserStream = stream;
            CancellerSource = new CancellationTokenSource();
            Listener = Task.Run(async () =>
            {
                while (IsListening)
                {
                    try
                    {
                        if (UserStream.AvailableFrames > 0)
                        {
                            // get level?
                            // if the level < threshold for time => speaker paused 
                            //      take the collected stream and send to Faster-Whisper-XXL
                            // else
                            //      add to collected stream
                            // should we chunk it more often? keep streaming to Faster-Whisper-XXL and see if it handles it?
                        }
                    }
                    catch (Exception ex)
                    {
                        SharedContext.Instance.Log(LogLevel.ERR, "AudioStream", $"Exception in listener for {UserId}: {ex.ToString()}");
                    }
                    await Task.Delay(1000, CancellerSource.Token);
                }
            }, CancellerSource.Token);
        }
    }
    public class AudioStreamer
    {
        public IAudioClient Client { get; set; }
        public bool IsStreaming { get; set; } = true;
        public AudioOutStream vcStream { get; set; }
        public Queue<string> Playlist { get; set; } = new Queue<string>();
        private Task StreamExecuter { get; set; }

        private Dictionary<ulong, InputStream> CurrentStreams = new Dictionary<ulong, InputStream>();

        public AudioStreamer(IAudioClient client, CancellationToken ct)
        {
            Client = client;
            Client.StreamCreated += Client_StreamCreated;
            Client.StreamDestroyed += Client_StreamDestroyed;
            vcStream = Client.CreatePCMStream(AudioApplication.Mixed, bufferMillis: 1000);
            // pre-populate the existing streams
            foreach (KeyValuePair<ulong, AudioInStream> pair in Client.GetStreams())
            {
                // get pair
                if (CurrentStreams.ContainsKey(pair.Key))
                {
                    // terminate thread
                    CurrentStreams[pair.Key].IsListening = false;
                    CurrentStreams[pair.Key].CancellerSource.Cancel();
                    // stop listening
                    // replace
                }
                // make a thread to "listen"
                // 
            }
            StreamExecuter = Task.Run(async () =>
            {
                while (IsStreaming)
                {
                    if (Playlist.Count == 0)
                    {
                        await Task.Delay(2000);
                    }
                    else
                    {
                        string path = Playlist.Dequeue();
                        // we have an audio client. make a PCM stream on it
                        Process p = GetFFmpeg(path);
                        using (Stream s = p.StandardOutput.BaseStream) {
                            // converted audio lives here?
                            byte[] buffer = new byte[4096];
                            int bytesReturned = 1;
                            await Client.SetSpeakingAsync(true);
                            while (bytesReturned > 0)
                            {
                                bytesReturned = s.Read(buffer, 0, buffer.Length);
                                if (bytesReturned > 0)
                                    vcStream.Write(buffer, 0, bytesReturned);
                                //SharedContext.Instance.Log(LogLevel.INFO, "Discord", $"Streamed {bytesReturned} bytes");
                            }
                            // close the stream after we're done with it.
                            await vcStream.FlushAsync();
                            await Client.SetSpeakingAsync(false);
                        }
                    }
                }
            }, ct);
        }

        private async Task Client_StreamDestroyed(ulong userId)
        {
            if(CurrentStreams.ContainsKey(userId))
                CurrentStreams.Remove(userId);
        }

        private async Task Client_StreamCreated(ulong userId, AudioInStream stream)
        {
            //if(CurrentStreams.ContainsKey(userId))
            //    CurrentStreams[userId] = stream;
            //else
            //    CurrentStreams.Add(userId, stream);

            // start listening in a loop?
            // make a task?
            // store this in CurrentStreams as well?
        }
        public void Enqueue(string path)
        {
            Playlist.Enqueue(path);
        }
        private Process GetFFmpeg(string path)
        {
            return Process.Start(new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-hide_banner -loglevel panic -i \"{path}\" -ac 2 -f s16le -ar 48000 pipe:1",
                UseShellExecute = false,
                RedirectStandardOutput = true,
            });
        }
    }
}
