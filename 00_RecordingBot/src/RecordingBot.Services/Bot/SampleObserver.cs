// using Microsoft.Graph.Communications.Common.Telemetry;
// using System;

// namespace RecordingBot.Services.Bot{
//         public class SampleObserver : IObserver<LogEvent>, IDisposable
//     {
//         public SampleObserver(IGraphLogger logger, string vLobbyDomain, string vLobbyKey);

//         public void Dispose();
//         public string GetLogs(int skip = 0, int take = int.MaxValue);
//         public string GetLogs(string filter, int skip = 0, int take = int.MaxValue);
//         public void OnCompleted();
//         public void OnError(Exception error);
//         public void OnNext(LogEvent logEvent);
//     }
// }