using Microsoft.Skype.Bots.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RecordingBot.Services.Bot{
       public class MediaPayload
    {
        public byte[] Data { get; set; }
        public long Timestamp { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public VideoColorFormat ColorFormat { get; set; }
        public float FrameRate { get; set; }
        public String Event { get; set; }
        public string UserId { get; set; }
        public string DisplayName { get; set; }
    } 
}