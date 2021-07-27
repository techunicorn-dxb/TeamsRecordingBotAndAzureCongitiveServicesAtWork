using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RecordingBot.Services.Bot{
        public class AudioPayload
    {
        public byte[] Data { get; set; }
        public long Timestamp { get; set; }
        public long Length { get; set; }
    }
}