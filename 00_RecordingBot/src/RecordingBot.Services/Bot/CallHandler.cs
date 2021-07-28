// ***********************************************************************
// Assembly         : RecordingBot.Services
// Author           : JasonTheDeveloper
// Created          : 09-07-2020
//
// Last Modified By : dannygar
// Last Modified On : 09-07-2020
// ***********************************************************************
// <copyright file="CallHandler.cs" company="Microsoft">
//     Copyright �  2020
// </copyright>
// <summary></summary>
// ***********************************************************************>

using Microsoft.Graph;
using Microsoft.Graph.Communications.Calls;
using Microsoft.Graph.Communications.Calls.Media;
using Microsoft.Graph.Communications.Common.Telemetry;
using Microsoft.Graph.Communications.Resources;
using RecordingBot.Model.Constants;
using RecordingBot.Services.Contract;
using RecordingBot.Services.ServiceSetup;
using RecordingBot.Services.Util;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using System.Timers;
using Microsoft.Skype.Bots.Media;
using System.Linq;
using Newtonsoft.Json;
//using Microsoft.Teams.Apps.Rise.Common.Repositories.MeetingData;



namespace RecordingBot.Services.Bot
{
    /// <summary>
    /// Call Handler Logic.
    /// </summary>
    public class CallHandler : HeartbeatHandler
    {
        /// <summary>
        /// Gets the call.
        /// </summary>
        /// <value>The call.</value>
        public ICall Call { get; }

        /// <summary>
        /// Gets the bot media stream.
        /// </summary>
        /// <value>The bot media stream.</value>
        public BotMediaStream BotMediaStream { get; private set; }

        /// <summary>
        /// The recording status index
        /// </summary>
        private int recordingStatusIndex = -1;

        /// <summary>
        /// The settings
        /// </summary>
        private readonly AzureSettings _settings;
        /// <summary>
        /// The event publisher
        /// </summary>
        private readonly IEventPublisher _eventPublisher;

        //new
        public const uint DominantSpeakerNone = DominantSpeakerChangedEventArgs.None;
        private readonly HashSet<uint> availableSocketIds = new HashSet<uint>();
        private Dictionary<string, string> userList = new Dictionary<string, string>();
        private string log = string.Empty;
        /// <summary>
        /// The capture
        /// </summary>
        private CaptureEvents _capture;
        private readonly object subscriptionLock = new object();

        /// <summary>
        /// The is disposed
        /// </summary>
        private bool _isDisposed = false;
        private readonly LRUCache currentVideoSubscriptions = new LRUCache(recorderConstants.NumberOfMultiviewSockets + 1);    
        private readonly ConcurrentDictionary<uint, uint> msiToSocketIdMapping = new ConcurrentDictionary<uint, uint>();   

        /// <summary>
        /// Initializes a new instance of the <see cref="CallHandler" /> class.
        /// </summary>
        /// <param name="statefulCall">The stateful call.</param>
        /// <param name="settings">The settings.</param>
        /// <param name="eventPublisher">The event publisher.</param>
        public CallHandler(
            ICall statefulCall,
            // string aTranscriptionLanguage,
            // string[] aTranslationLanguages,
            // IAzureSettings settings,
            // IEventPublisher eventPublisher,
            string name,
            IConfiguration configuration
        )
            : base(TimeSpan.FromMinutes(10), statefulCall?.GraphLogger)
        {
             Guid callId = new Guid();
            try
            {
                logDebug(0,"");
                callId = Guid.Parse(statefulCall.Id);
                this.Call = statefulCall;
                this.Call.OnUpdated += this.CallOnUpdated;

                // subscribe to dominant speaker event on the audioSocket
                var audioSocket = this.Call.GetLocalMediaSession().AudioSocket;
                audioSocket.DominantSpeakerChanged += this.OnDominantSpeakerChanged;

                // susbscribe to the participants updates, this will inform the bot if a particpant left/joined the conference
                this.Call.Participants.OnUpdated += this.ParticipantsOnUpdated;

                this.BotMediaStream = new BotMediaStream(this.Call.GetLocalMediaSession(), callId,null,null, this.GraphLogger, null,configuration,null, name);

                var sockets = this.Call.GetLocalMediaSession().VideoSockets;
                // [{"SocketId":0,"MediaType":1,"ReceiveColorFormat":3},{"SocketId":1,"MediaType":1,"ReceiveColorFormat":3},{"SocketId":2,"MediaType":1,"ReceiveColorFormat":3}]
                foreach (var socket in this.Call.GetLocalMediaSession().VideoSockets)
                {
                    this.availableSocketIds.Add((uint)socket.SocketId);
                }
            }
            catch (Exception e)
            {
                logDebug(1,e.Message);
                var innerMessage = e.InnerException == null ? string.Empty : e.InnerException.Message;
                var innerStack = e.InnerException == null ? string.Empty : e.InnerException.StackTrace;

                statefulCall.GraphLogger.Log(
                    System.Diagnostics.TraceLevel.Error,
                    $"type: {e.GetType()}\nmessage: {e.Message}\ntrace: {e.StackTrace}\ninner message: {innerMessage}\ntrace: {innerStack}",
                    memberName: VLobbyLogConstants.VLobbyLogError,
                    properties: new List<object> { new CallData { CallId = callId, MeetingId = statefulCall.Resource.ChatInfo.ThreadId } });

                this.Dispose();
                throw e;
            }
            // // _settings = (AzureSettings)settings;
            // // _eventPublisher = eventPublisher;

            // this.Call = statefulCall;
            // this.Call.OnUpdated += this.CallOnUpdated;
            // this.Call.Participants.OnUpdated += this.ParticipantsOnUpdated;     
            
            // this.GraphLogger.Log(System.Diagnostics.TraceLevel.Warning, $"Starting call with id: {this.Call.Id}");


            // // this.BotMediaStream = new BotMediaStream(this.Call.GetLocalMediaSession(), Guid.Parse(this.Call.Id),aTranscriptionLanguage, aTranslationLanguages, this.GraphLogger, eventPublisher, configuration ,_settings,name);            

            // if (_settings.CaptureEvents)
            // {
            //     var path = Path.Combine(Path.GetTempPath(), BotConstants.DefaultOutputFolder, _settings.EventsFolder, statefulCall.GetLocalMediaSession().MediaSessionId.ToString(), "participants");
            //     _capture = new CaptureEvents(path);
            // }
        }

        /// <inheritdoc/>
        protected override Task HeartbeatAsync(ElapsedEventArgs args)
        {
            return this.Call.KeepAliveAsync();
        }
        private void OnDominantSpeakerChanged(object sender, DominantSpeakerChangedEventArgs e)
        {
            this.GraphLogger.Info($"[{this.Call.Id}:OnDominantSpeakerChanged(DominantSpeaker={e.CurrentDominantSpeaker})]");
            this.log += $"[{this.Call.Id}:OnDominantSpeakerChanged(DominantSpeaker={e.CurrentDominantSpeaker})]\n";

            if (e.CurrentDominantSpeaker != DominantSpeakerNone)
            {
                IParticipant participant = this.GetParticipantFromMSI(e.CurrentDominantSpeaker);
                var participantDetails = participant?.Resource?.Info?.Identity?.User;
                if (participantDetails != null)
                {
                    // we want to force the video subscription on dominant speaker events
                    // this.SubscribeToParticipantVideo(participant, forceSubscribe: true);
                }
            }
        }
        #region new
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            this.log += "Disposing\n";

            var audioSocket = this.Call.GetLocalMediaSession().AudioSocket;
            audioSocket.DominantSpeakerChanged -= this.OnDominantSpeakerChanged;

            this.Call.OnUpdated -= this.CallOnUpdated;
            this.Call.Participants.OnUpdated -= this.ParticipantsOnUpdated;

            foreach (var participant in this.Call.Participants)
            {
                participant.OnUpdated -= this.OnParticipantUpdated;
            }

            /*
            this.recordingStatusFlipTimer.Enabled = false;
            this.recordingStatusFlipTimer.Elapsed -= this.OnRecordingStatusFlip;
            */
            this.BotMediaStream.Dispose();
            string userListJson = JsonConvert.SerializeObject(this.userList);
            System.IO.File.WriteAllText($"C:\\TEst\\{this.Call.Id}\\userList.json", userListJson);
        }
         private void OnParticipantUpdated(IParticipant sender, ResourceEventArgs<Participant> args)
        {
            log += "OnParticipantUpdated" + "\n";
            if (args.OldResource.MediaStreams.Where(x => (x.MediaType == Modality.Video) && (x.Direction == MediaDirection.ReceiveOnly || x.Direction == MediaDirection.Inactive)).FirstOrDefault() != null &&
                args.NewResource.MediaStreams.Where(x => x.MediaType == Modality.Video && (x.Direction == MediaDirection.SendReceive || x.Direction == MediaDirection.SendOnly)).FirstOrDefault() != null)
            {
                log += "\tSubscibe video event detected" + "\n";
                this.SubscribeToParticipantVideo(sender, forceSubscribe: false);
            }

            else if (args.OldResource.MediaStreams.Where(x => x.MediaType == Modality.Video && (x.Direction == MediaDirection.SendReceive || x.Direction == MediaDirection.SendOnly)).FirstOrDefault() != null &&
                args.NewResource.MediaStreams.Where(x => x.MediaType == Modality.Video && (x.Direction == MediaDirection.ReceiveOnly || x.Direction == MediaDirection.Inactive)).FirstOrDefault() != null)
            {
                log += "\tUnsubscribe video event detected" + "\n";
                this.UnsubscribeFromParticipantVideo(sender);
            }

            else if (args.OldResource.MediaStreams.Where(x => (x.MediaType == Modality.VideoBasedScreenSharing) && (x.Direction == MediaDirection.ReceiveOnly || x.Direction == MediaDirection.Inactive)).FirstOrDefault() != null &&
                args.NewResource.MediaStreams.Where(x => x.MediaType == Modality.VideoBasedScreenSharing && (x.Direction == MediaDirection.SendReceive || x.Direction == MediaDirection.SendOnly)).FirstOrDefault() != null)
            {
                log += "\tSubscibe vbss event detected" + "\n";
                this.SubscribeToParticipantVideo(sender, forceSubscribe: false);
            }

            else if (args.OldResource.MediaStreams.Where(x => x.MediaType == Modality.VideoBasedScreenSharing && (x.Direction == MediaDirection.SendReceive || x.Direction == MediaDirection.SendOnly)).FirstOrDefault() != null &&
                args.NewResource.MediaStreams.Where(x => x.MediaType == Modality.VideoBasedScreenSharing && (x.Direction == MediaDirection.ReceiveOnly || x.Direction == MediaDirection.Inactive)).FirstOrDefault() != null)
            {
                log += "\tUnsubscribe vbss event detected" + "\n";
                this.UnsubscribeFromParticipantVideo(sender);
            }
        }
        private void SubscribeToParticipantVideo(IParticipant participant, bool forceSubscribe = true)
        {
            // string log = string.Empty;
            log += "participant video subscribe" + "\n";

            foreach (var stream in participant.Resource.MediaStreams)
            {
                this.log += "\t" + "Resource: " + stream.MediaType + "\n";
                this.log += "\t\t" + "Direction: " + stream.Direction + "\n";
            }

            try
            {
                bool subscribeToVideo = false;
                uint socketId = uint.MaxValue;

                // filter the mediaStreams to see if the participant has a video send
                var participantSendCapableVideoStream = participant.Resource.MediaStreams.Where(x => x.MediaType == Modality.Video &&
                   (x.Direction == MediaDirection.SendReceive || x.Direction == MediaDirection.SendOnly)).FirstOrDefault();
                if (participantSendCapableVideoStream != null)
                {
                    log += "\t" + "participantSendCapableVideoStream is not null Label: " + participantSendCapableVideoStream.Label + "\n";
                    bool updateMSICache = false;
                    var msi = uint.Parse(participantSendCapableVideoStream.SourceId);
                    lock (this.subscriptionLock)
                    {
                        if (this.currentVideoSubscriptions.Count < this.Call.GetLocalMediaSession().VideoSockets.Count)
                        {
                            log += "\t" + "currentVideoSubscriptions.Count < this.Call.GetLocalMediaSession().VideoSockets.Count" + "\n";

                            // we want to verify if we already have a socket subscribed to the MSI
                            if (!this.msiToSocketIdMapping.ContainsKey(msi))
                            {
                                log += "\t" + "!this.msiToSocketIdMapping.ContainsKey(msi)" + "\n";
                                if (this.availableSocketIds.Any())
                                {
                                    log += "\t" + "this.availableSocketIds.Any()" + "\n";
                                    socketId = this.availableSocketIds.Last();
                                    this.availableSocketIds.Remove((uint)socketId);
                                    subscribeToVideo = true;
                                }
                            }

                            updateMSICache = true;
                            this.GraphLogger.Info($"[{this.Call.Id}:SubscribeToParticipant(socket {socketId} available, the number of remaining sockets is {this.availableSocketIds.Count}, subscribing to the participant {participant.Id})");
                            this.log += $"[{this.Call.Id}:SubscribeToParticipant(socket {socketId} available, the number of remaining sockets is {this.availableSocketIds.Count}, subscribing to the participant {participant.Id})\n";
                        }
                        else if (forceSubscribe)
                        {
                            log += "\t" + "forceSubscribe" + "\n";
                            // here we know that all the sockets subscribed to a video we need to update the msi cache,
                            // and obtain the socketId to reuse with the new MSI
                            updateMSICache = true;
                            subscribeToVideo = true;
                        }

                        if (updateMSICache)
                        {
                            log += "\t" + "updateMSICache" + "\n";
                            this.currentVideoSubscriptions.TryInsert(msi, out uint? dequeuedMSIValue);
                            if (dequeuedMSIValue != null)
                            {
                                // Cache was updated, we need to use the new available socket to subscribe to the MSI
                                this.msiToSocketIdMapping.TryRemove((uint)dequeuedMSIValue, out socketId);
                            }
                        }
                    }
                    log += "\t" + subscribeToVideo.ToString() + " " + socketId + " " + "\n";
                    if (subscribeToVideo && socketId != uint.MaxValue)
                    {
                        var participantDetails = participant.Resource.Info.Identity.User;
                        if (participantDetails == null)
                        {
                            participantDetails = participant.Resource.Info.Identity.GetGuest();
                        }

                        log += "\t" + "subscribeToVideo && socketId != uint.MaxValue" + "\n";
                        this.msiToSocketIdMapping.AddOrUpdate(msi, socketId, (k, v) => socketId);

                        this.GraphLogger.Info($"[{this.Call.Id}:SubscribeToParticipant(subscribing to the participant {participant.Id} on socket {socketId})");
                        this.BotMediaStream.Subscribe(MediaType.Video, msi, VideoResolution.HD1080p, participantDetails, socketId);
                    }
                }

                // vbss viewer subscription
                var vbssParticipant = participant.Resource.MediaStreams.SingleOrDefault(x => x.MediaType == Modality.VideoBasedScreenSharing
                && x.Direction == MediaDirection.SendOnly);
                if (vbssParticipant != null)
                {
                    var participantDetails = participant.Resource.Info.Identity.User;
                    if (participantDetails == null)
                    {
                        participantDetails = participant.Resource.Info.Identity.GetGuest();
                    }

                    log += "\t" + "vbssParticipant != null" + "\n";
                    // new sharer
                    this.GraphLogger.Info($"[{this.Call.Id}:SubscribeToParticipant(subscribing to the VBSS sharer {participant.Id})");
                    this.BotMediaStream.Subscribe(MediaType.Vbss, uint.Parse(vbssParticipant.SourceId), VideoResolution.HD1080p, participantDetails, socketId);
                }
            }
            catch(Exception e )
            {
                System.IO.File.WriteAllText("C:\\TEst\\calllog-err" + DateTime.UtcNow.Ticks + ".txt", $"[ERROR] {e.Message}\n{e.InnerException}\n{e.InnerException}");
            }

            // System.IO.File.WriteAllText("C:\\TEst\\subscribe-log" + DateTime.UtcNow.Ticks + ".txt", log);
        }
        private void UnsubscribeFromParticipantVideo(IParticipant participant)
        {
            var participantSendCapableVideoStream = participant.Resource.MediaStreams.Where(x => x.MediaType == Modality.Video &&
              (x.Direction == MediaDirection.ReceiveOnly || x.Direction == MediaDirection.Inactive)).FirstOrDefault();

            // string log = string.Empty;
            log += "participant video unsubscribe" + "\n";

            if (participantSendCapableVideoStream != null)
            {
                log += "\t" + "participantSendCapableVideoStream != null" + "\n";
                var msi = uint.Parse(participantSendCapableVideoStream.SourceId);
                lock (this.subscriptionLock)
                {
                    if (this.currentVideoSubscriptions.TryRemove(msi))
                    {
                        log += "\t" + "this.currentVideoSubscriptions.TryRemove(msi)" + "\n";
                        if (this.msiToSocketIdMapping.TryRemove(msi, out uint socketId))
                        {
                            log += "\t" + "this.msiToSocketIdMapping.TryRemove(msi, out uint socketId)" + "\n";
                            this.BotMediaStream.Unsubscribe(MediaType.Video, socketId);
                            this.availableSocketIds.Add(socketId);
                        }
                    }
                }
            }
            // System.IO.File.WriteAllText("C:\\TEst\\unsubscribe-log" + DateTime.UtcNow.Ticks + ".txt", log);
        }
        private IParticipant GetParticipantFromMSI(uint msi)
        {
            return this.Call.Participants.SingleOrDefault(x => x.Resource.IsInLobby == false && x.Resource.MediaStreams.Any(y => y.SourceId == msi.ToString()));
        }
        #endregion

        /// <inheritdoc />
        // protected override void Dispose(bool disposing)
        // {

        //     base.Dispose(disposing);
        //     _isDisposed = true;
        //     this.Call.OnUpdated -= this.CallOnUpdated;
        //     this.Call.Participants.OnUpdated -= this.ParticipantsOnUpdated;

        //     this.BotMediaStream?.Dispose();            

        //     // Event - Dispose of the call completed ok
        //     _eventPublisher.Publish("CallDisposedOK", $"Call.Id: {this.Call.Id}");
        // }

        /// <summary>
        /// Called when recording status flip timer fires.
        /// </summary>
        /// <param name="source">The <see cref="ICall" /> source.</param>
        /// <param name="e">The <see cref="ElapsedEventArgs" /> instance containing the event data.</param>
        private void OnRecordingStatusFlip(ICall source, ElapsedEventArgs e)
        {
            _ = Task.Run(async () =>
            {
                // TODO: consider rewriting the recording status checking
                var recordingStatus = new[] { RecordingStatus.Recording, RecordingStatus.NotRecording, RecordingStatus.Failed };

                var recordingIndex = this.recordingStatusIndex + 1;
                if (recordingIndex >= recordingStatus.Length)
                {
                    var recordedParticipantId = this.Call.Resource.IncomingContext.ObservedParticipantId;

                    var recordedParticipant = this.Call.Participants[recordedParticipantId];
                    await recordedParticipant.DeleteAsync().ConfigureAwait(false);
                    // Event - Recording has ended
                     _eventPublisher.Publish("CallRecordingFlip", $"Call.Id: {Call.Id} ended");
                    return;
                }

                var newStatus = recordingStatus[recordingIndex];
                try
                {
                    // Event - Log the recording status
                    var status = Enum.GetName(typeof(RecordingStatus), newStatus);
                    _eventPublisher.Publish("CallRecordingFlip", $"Call.Id: {Call.Id} status changed to {status}");

                    // NOTE: if your implementation supports stopping the recording during the call, you can call the same method above with RecordingStatus.NotRecording
                    await source
                        .UpdateRecordingStatusAsync(newStatus)
                        .ConfigureAwait(false);

                    this.recordingStatusIndex = recordingIndex;
                }
                catch (Exception exc)
                {
                    // e.g. bot joins via direct join - may not have the permissions
                    GraphLogger.Error(exc, $"Failed to flip the recording status to {newStatus}");
                    // Event - Recording status exception - failed to update 
                    _eventPublisher.Publish("CallRecordingFlip", $"Failed to flip the recording status to {newStatus}");
                }
            }).ForgetAndLogExceptionAsync(this.GraphLogger);
        }

        /// <summary>
        /// Event fired when the call has been updated.
        /// </summary>
        /// <param name="sender">The call.</param>
        /// <param name="e">The event args containing call changes.</param>
        private void CallOnUpdated(ICall sender, ResourceEventArgs<Call> e)
        {
            // GraphLogger.Info($"Call status updated to {e.NewResource.State} - {e.NewResource.ResultInfo?.Message}");
            // // Event - Recording update e.g established/updated/start/ended
            // _eventPublisher.Publish($"Call{e.NewResource.State}", $"Call.ID {Call.Id} Sender.Id {sender.Id} status updated to {e.NewResource.State} - {e.NewResource.ResultInfo?.Message}");

            // if (e.OldResource.State != e.NewResource.State && e.NewResource.State == CallState.Established)
            // {
            //     if (!_isDisposed)
            //     {
            //         // Call is established. We should start receiving Audio, we can inform clients that we have started recording.
            //         OnRecordingStatusFlip(sender, null);
            //     }
            // }

            // if ((e.OldResource.State == CallState.Established) && (e.NewResource.State == CallState.Terminated))
            // {
            //     if (BotMediaStream != null)
            //     {
            //        var aQoE = BotMediaStream.GetAudioQualityOfExperienceData();

            //         if (aQoE != null)
            //         {
            //             if (_settings.CaptureEvents)
            //                 await _capture?.Append(aQoE);
            //         }
            //         await BotMediaStream.StopMedia();
            //     }

            //     if (_settings.CaptureEvents)
            //         await _capture?.Finalise();
            // }
            if (e.OldResource.State != e.NewResource.State && e.NewResource.State == CallState.Established)
            {
                // Call is established. We should start receiving Audio, we can inform clients that we have started recording.
                this.OnRecordingStatusFlip(sender, null);

                // for testing purposes, flip the recording status automatically at intervals
                // this.recordingStatusFlipTimer.Enabled = true;
            }
        }

        /// <summary>
        /// Creates the participant update json.
        /// </summary>
        /// <param name="participantId">The participant identifier.</param>
        /// <param name="participantDisplayName">Display name of the participant.</param>
        /// <returns>System.String.</returns>
        private string createParticipantUpdateJson(string participantId, string participantDisplayName = "")
        {
            if (participantDisplayName.Length==0)
                return "{" + String.Format($"\"Id\": \"{participantId}\"") + "}";
            else
                return "{" + String.Format($"\"Id\": \"{participantId}\", \"DisplayName\": \"{participantDisplayName}\"") + "}";
        }

        /// <summary>
        /// Updates the participant.
        /// </summary>
        /// <param name="participants">The participants.</param>
        /// <param name="participant">The participant.</param>
        /// <param name="added">if set to <c>true</c> [added].</param>
        /// <param name="participantDisplayName">Display name of the participant.</param>
        /// <returns>System.String.</returns>
        private string updateParticipant(List<IParticipant> participants, IParticipant participant, bool added, string participantDisplayName = "")
        {
            if (added)
                participants.Add(participant);
            else
                participants.Remove(participant);
            return createParticipantUpdateJson(participant.Id, participantDisplayName);
        }

        /// <summary>
        /// Updates the participants.
        /// </summary>
        /// <param name="eventArgs">The event arguments.</param>
        /// <param name="added">if set to <c>true</c> [added].</param>
        private void updateParticipants(ICollection<IParticipant> eventArgs, bool added = true)
        {
            foreach (var participant in eventArgs)
            {
                var json = string.Empty;

                // todo remove the cast with the new graph implementation,
                // for now we want the bot to only subscribe to "real" participants
                var participantDetails = participant.Resource.Info.Identity.User;

                if (participantDetails != null)
                {
                    json = updateParticipant(this.BotMediaStream.participants, participant, added, participantDetails.DisplayName);
                }
                else if (participant.Resource.Info.Identity.AdditionalData?.Count > 0)
                {
                    if (CheckParticipantIsUsable(participant))
                    {
                        json = updateParticipant(this.BotMediaStream.participants, participant, added);
                    }
                }

               if (json.Length > 0)
                    if (added)
                        _eventPublisher.Publish("CallParticipantAdded", json);
                    else
                        _eventPublisher.Publish("CallParticipantRemoved", json);
            }
        }

        /// <summary>
        /// Event fired when the participants collection has been updated.
        /// </summary>
        /// <param name="sender">Participants collection.</param>
        /// <param name="args">Event args containing added and removed participants.</param>
        public void ParticipantsOnUpdated(IParticipantCollection sender, CollectionEventArgs<IParticipant> args)
        {
            if (_settings.CaptureEvents)
            {
                _capture?.Append(args);
            }
            updateParticipants(args.AddedResources);
            updateParticipants(args.RemovedResources, false);
        }

        /// <summary>
        /// Checks the participant is usable.
        /// </summary>
        /// <param name="p">The p.</param>
        /// <returns><c>true</c> if XXXX, <c>false</c> otherwise.</returns>
        private bool CheckParticipantIsUsable(IParticipant p)
        {
            foreach (var i in p.Resource.Info.Identity.AdditionalData)
                if (i.Key != "applicationInstance" && i.Value is Identity)
                    return true;

            return false;
        }

        #region functions used for debugging...
        public static async Task logDebug(int code,String err)
        {
            try { 
        using StreamWriter file = new("Totallogs.txt", append: true);
            switch (code) { 
            case 0:
                await file.WriteLineAsync("Inisde Constructor");
                break;
            case 1:
                await file.WriteLineAsync("ERROR "+err);
                break;
            default:
                await file.WriteLineAsync("idk")
                break;
            }
                }
            catch(Exception e) { 
                  
            }
        }
        #endregion
    }
}
