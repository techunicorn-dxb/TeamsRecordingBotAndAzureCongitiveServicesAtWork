
// using System;
// using System.Diagnostics;
// using System.Runtime.CompilerServices;
// using System.Threading.Tasks;
 
// namespace RecordingBot.Services.Bot{
//         public class VirtualLobbyRepository
//     {
//         public VirtualLobbyRepository(string domain, string key);

//         [AsyncStateMachine(typeof(<LogRecordingEndAsync>d__4))]
//         [DebuggerStepThrough]
//         public Task LogRecordingEndAsync(Guid callId, string meetingId);
//         [AsyncStateMachine(typeof(<LogRecordingErrorAsync>d__5))]
//         [DebuggerStepThrough]
//         public Task LogRecordingErrorAsync(string message, Guid callId, string meetingId);
//         [AsyncStateMachine(typeof(<LogRecordingStartAsync>d__3))]
//         [DebuggerStepThrough]
//         public Task<bool> LogRecordingStartAsync(Guid callId, string meetingId);
//     }
// }