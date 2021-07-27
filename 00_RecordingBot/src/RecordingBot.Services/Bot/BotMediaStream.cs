// ***********************************************************************
// Assembly         : RecordingBot.Services
// Author           : JasonTheDeveloper
// Created          : 09-07-2020
//
// Last Modified By : dannygar
// Last Modified On : 09-07-2020
// ***********************************************************************
// <copyright file="BotMediaStream.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.
// </copyright>
// <summary>The bot media stream.</summary>
// ***********************************************************************-

using Microsoft.Graph.Communications.Calls;
using Microsoft.Graph.Communications.Calls.Media;
using Microsoft.Graph.Communications.Common;
using Microsoft.Graph.Communications.Common.Telemetry;
using Microsoft.Skype.Bots.Media;
using Microsoft.Skype.Internal.Media.Services.Common;
using RecordingBot.Services.Contract;
using RecordingBot.Services.Media;
using RecordingBot.Services.ServiceSetup;
using RecordingBot.Services.Util;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.Text;


namespace RecordingBot.Services.Bot
{
    /// <summary>
    /// Class responsible for streaming audio and video.
    /// </summary>
    public class BotMediaStream : ObjectRootDisposable
    {
        /// <summary>
        /// The participants
        /// </summary>
        internal List<IParticipant> participants;

        /// <summary>
        /// The audio socket
        /// </summary>
        private readonly IAudioSocket _audioSocket;
        /// <summary>
        /// The media stream
        /// </summary>
        private readonly IMediaStream _mediaStream;
        /// <summary>
        /// The event publisher
        /// </summary>
        private readonly IEventPublisher _eventPublisher;

        /// <summary>
        /// The settings
        /// </summary>
        private readonly AzureSettings _settings;

        /// <summary>
        /// The call identifier
        /// </summary>
        private readonly Guid _callId;

        #region Added Vars
        private IConfiguration configuration;
        private readonly string meetingId;
        private readonly List<IVideoSocket> videoSockets;
        private string appData;
        private readonly ILocalMediaSession mediaSession;
        private readonly IVideoSocket vbssSocket;
        private Dictionary<int, string> socketUserMapping;
        private Dictionary<string, List<MediaPayload>> userVideoData;
        private List<AudioPayload> audioData;
        private List<MediaPayload> vbssData;
        private string log;
        #endregion
        #region old stuff
        private readonly string mTranscriptionLanguage;
        private readonly string[] mTranslationLanguages;

        private Dictionary<uint, MySTT> mSpeechToTextPool = new Dictionary<uint, MySTT>();

        /// <summary>
        /// Return the last read 'audio quality of experience data' in a serializable structure
        /// </summary>
        /// <value>The audio quality of experience data.</value>
        public SerializableAudioQualityOfExperienceData AudioQualityOfExperienceData { get; private set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="BotMediaStream" /> class.
        /// </summary>
        /// <param name="mediaSession">he media session.</param>
        /// <param name="callId">The call identity</param>
        /// <param name="logger">The logger.</param>
        /// <param name="eventPublisher">Event Publisher</param>
        /// <param name="settings">Azure settings</param>
        /// <exception cref="InvalidOperationException">A mediaSession needs to have at least an audioSocket</exception>
        #endregion
        public BotMediaStream(
            ILocalMediaSession mediaSession,
            Guid callId,
            string aTranscriptionLanguage,
            string[] aTranslationLanguages,
            IGraphLogger logger,
            IEventPublisher eventPublisher,
            IConfiguration configuration,
            IAzureSettings settings,
            string meetingId
        )
            : base(logger)
        {

            this.configuration = configuration;
            try
            {
                this._callId= callId;
                this.meetingId = string.IsNullOrWhiteSpace(meetingId) ? string.Empty : meetingId;
                this.appData = "C:\\TEst";
                System.IO.Directory.CreateDirectory($"{appData}\\{callId}");

                ArgumentVerifier.ThrowOnNullArgument(mediaSession, nameof(mediaSession));
                ArgumentVerifier.ThrowOnNullArgument(logger, nameof(logger));

                this.mediaSession = mediaSession;

                // Subscribe to the audio media.
                this._audioSocket = mediaSession.AudioSocket;
                if (this._audioSocket == null)
                {
                    throw new InvalidOperationException("A mediaSession needs to have at least an audioSocket");
                }

                this._audioSocket.AudioMediaReceived += this.OnAudioMediaReceived;

                // Subscribe to the video media.
                this.videoSockets = this.mediaSession.VideoSockets?.ToList();

                // videoParticipants.AddRange(new uint[this.videoSockets.Count()]);
                if (this.videoSockets?.Any() == true)
                {
                    //TODO Add the function for onvideo recevied
                    this.videoSockets.ForEach(videoSocket => videoSocket.VideoMediaReceived += this.OnVideoMediaReceived);
                }

                // Subscribe to the VBSS media.
                this.vbssSocket = this.mediaSession.VbssSocket;
                if (this.vbssSocket != null)
                {
                    //TODO Add the function for onvb whatever recevied
                    this.mediaSession.VbssSocket.VideoMediaReceived += this.OnVbssMediaReceived;
                }

                this.GraphLogger.Log(
                    System.Diagnostics.TraceLevel.Info,
                    $"Recording Started",
                    memberName: VLobbyLogConstants.VLobbyLogRecStarted,
                    properties: new List<object> { new  CallData { CallId = this._callId, MeetingId = this.meetingId } });

            }
            catch (Exception e)
            {
                var innerMessage = e.InnerException == null ? string.Empty : e.InnerException.Message;
                var innerStack = e.InnerException == null ? string.Empty : e.InnerException.StackTrace;

                this.GraphLogger.Log(
                    System.Diagnostics.TraceLevel.Error,
                    $"type: {e.GetType()}\nmessage: {e.Message}\ntrace: {e.StackTrace}\ninner message: {innerMessage}\ntrace: {innerStack}",
                    memberName: VLobbyLogConstants.VLobbyLogError,
                    properties: new List<object> { new  CallData { CallId = this._callId, MeetingId = this.meetingId } });

                throw e;
            }

            this.vbssData = new List<MediaPayload>();
            this.socketUserMapping = new Dictionary<int, string>();
            this.userVideoData = new Dictionary<string, List<MediaPayload>>();
            this.audioData = new List<AudioPayload>();

            this.log = string.Empty;
            this.log += DateTimeOffset.UtcNow.ToString() + " started log 1.7!\n";
            
            // ArgumentVerifier.ThrowOnNullArgument(mediaSession, nameof(mediaSession));
            // ArgumentVerifier.ThrowOnNullArgument(logger, nameof(logger));
            // ArgumentVerifier.ThrowOnNullArgument(settings, nameof(settings));

            // this.participants = new List<IParticipant>();

            // this.mTranscriptionLanguage = aTranscriptionLanguage;
            // this.mTranslationLanguages = aTranslationLanguages;

            // _eventPublisher = eventPublisher;
            // _callId = callId;
            // _settings = (AzureSettings)settings;
            // _mediaStream = new MediaStream(
            //     settings,
            //     logger,
            //     mediaSession.MediaSessionId.ToString()
            // );

            // // Subscribe to the audio media.
            // this._audioSocket = mediaSession.AudioSocket;
            // if (this._audioSocket == null)
            // {
            //     throw new InvalidOperationException("A mediaSession needs to have at least an audioSocket");
            // }

            // this._audioSocket.AudioMediaReceived += this.OnAudioMediaReceived;
        //TODO: Add this back in when we have a way to get the media stream quality of experience data

        }


        /// <summary>
        /// Gets the participants.
        /// </summary>
        /// <returns>List&lt;IParticipant&gt;.</returns>
        public List<IParticipant> GetParticipants()
        {
            return participants;
        }

        /// <summary>
        /// Gets the audio quality of experience data.
        /// </summary>
        /// <returns>SerializableAudioQualityOfExperienceData.</returns>
        public SerializableAudioQualityOfExperienceData GetAudioQualityOfExperienceData()
        {
            AudioQualityOfExperienceData = new SerializableAudioQualityOfExperienceData(this._callId.ToString(), this._audioSocket.GetQualityOfExperienceData());
            return AudioQualityOfExperienceData;
        }

        /// <summary>
        /// Stops the media.
        /// </summary>
        public async Task StopMedia()
        {
            await _mediaStream.End();
            // Event - Stop media occurs when the call stops recording
            _eventPublisher.Publish("StopMediaStream", "Call stopped recording");
        }

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
             try
            {
                base.Dispose(disposing);

                this._audioSocket.AudioMediaReceived -= this.OnAudioMediaReceived;

                if (this.videoSockets?.Any() == true)
                {
                    this.videoSockets.ForEach(videoSocket => videoSocket.VideoMediaReceived -= this.OnVideoMediaReceived);
                }

                // Subscribe to the VBSS media.
                if (this.vbssSocket != null)
                {
                    this.mediaSession.VbssSocket.VideoMediaReceived -= this.OnVbssMediaReceived;
                }
            }
            catch (Exception e)
            {
                this.log += $"Exception {e.Message}\n";
                this.log += DateTimeOffset.UtcNow.ToString() + " Failed: " + e.Message + " " + e.InnerException + "\n";

                var innerMessage = e.InnerException == null ? string.Empty : e.InnerException.Message;
                var innerStack = e.InnerException == null ? string.Empty : e.InnerException.StackTrace;

                this.GraphLogger.Log(
                    System.Diagnostics.TraceLevel.Error,
                    $"type: {e.GetType()}\nmessage: {e.Message}\ntrace: {e.StackTrace}\ninner message: {innerMessage}\ntrace: {innerStack}",
                    memberName: VLobbyLogConstants.VLobbyLogError,
                    properties: new List<object> { new CallData { CallId = this._callId, MeetingId = this.meetingId } });
            }

            try
            {
                // Saving raw vbss
                string vbssJson = JsonConvert.SerializeObject(this.vbssData);
                System.IO.File.WriteAllText($"{this.appData}\\{this._callId}\\vbssData.json", vbssJson);

                var config = new CallMediaSessionConfig();
                config.Users = new List<string>();
                foreach (var key in this.userVideoData.Keys)
                {
                    var filename = MakeValidFileName(key);
                    // Saving raw video
                    string videoJson = JsonConvert.SerializeObject(this.userVideoData[key]);
                    byte[] videoBytes = Encoding.UTF8.GetBytes(videoJson);

                    System.IO.File.WriteAllText($"{this.appData}\\{this._callId}\\{filename}.json", videoJson);
                    config.Users.Add(filename);
                }

                // Saving config
                string configJson = JsonConvert.SerializeObject(config);
                System.IO.File.WriteAllText($"{this.appData}\\{this._callId}\\config.json", configJson);

                // Saving raw audio
                string audioJson = JsonConvert.SerializeObject(this.audioData);
                System.IO.File.WriteAllText($"{this.appData}\\{this._callId}\\audioData.json", audioJson);

                // - Saving meeting info
                var meetingInfo = new MeetingInfo { MeetingId = this.meetingId, MeetingName = this.meetingId };
                string meetingInfoJson = JsonConvert.SerializeObject(meetingInfo);
                System.IO.File.WriteAllText($"{this.appData}\\{this._callId}\\meetinginfo.json", meetingInfoJson);

                this.GraphLogger.Log(
                    System.Diagnostics.TraceLevel.Info,
                    $"Recording Ended",
                    memberName: VLobbyLogConstants.VLobbyLogRecEnded,
                    properties: new List<object> { new  CallData { CallId = this._callId, MeetingId = this.meetingId } });
            }
            catch (Exception e)
            {
                this.log += $"Exception {e.Message}\n";
                this.log += DateTimeOffset.UtcNow.ToString() + " Failed: " + e.Message + " " + e.InnerException + "\n";

                var innerMessage = e.InnerException == null ? string.Empty : e.InnerException.Message;
                var innerStack = e.InnerException == null ? string.Empty : e.InnerException.StackTrace;

                this.GraphLogger.Log(
                    System.Diagnostics.TraceLevel.Error,
                    $"type: {e.GetType()}\nmessage: {e.Message}\ntrace: {e.StackTrace}\ninner message: {innerMessage}\ntrace: {innerStack}",
                    memberName: VLobbyLogConstants.VLobbyLogError,
                    properties: new List<object> { new  CallData { CallId = this._callId, MeetingId = this.meetingId } });
            }

            // - Saving logs
            System.IO.File.WriteAllText($"{this.appData}\\{this._callId}\\logs.txt", this.log);
            #region :)
            // Event Dispose of the bot media stream object
            _eventPublisher.Publish("MediaStreamDispose", disposing.ToString());

            base.Dispose(disposing);

            this._audioSocket.AudioMediaReceived -= this.OnAudioMediaReceived;
            #endregion
        }

        /// <summary>
        /// Receive audio from subscribed participant.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="e">The audio media received arguments.</param>
        #region the good stuff
        public void Subscribe(MediaType mediaType, uint mediaSourceId, VideoResolution videoResolution, Microsoft.Graph.Identity participant, uint socketId = 0)
        {
            this.log += DateTimeOffset.UtcNow.ToString() + $" Subscribe has been called {socketId}\n";
            try
            {
                this.ValidateSubscriptionMediaType(mediaType);

                this.GraphLogger.Info($"Subscribing to the video source: {mediaSourceId} on socket: {socketId} with the preferred resolution: {videoResolution} and mediaType: {mediaType}");
                if (mediaType == MediaType.Vbss)
                {
                    if (this.vbssSocket == null)
                    {
                        this.GraphLogger.Warn($"vbss socket not initialized");
                        this.log += DateTimeOffset.UtcNow.ToString() + " vbss socket not initialized\n";
                    }
                    else
                    {
                        this.vbssSocket.Subscribe(videoResolution, mediaSourceId);
                        this.log += DateTimeOffset.UtcNow.ToString() + " Subscribed vbss\n";
                        this.vbssData.Add(new MediaPayload
                        {
                            Data = null,
                            Timestamp = DateTime.UtcNow.Ticks,
                            Width = 0,
                            Height = 0,
                            ColorFormat = VideoColorFormat.H264,
                            FrameRate = 0,
                            Event = "Subscribed",
                            UserId = participant.Id,
                            DisplayName = participant.DisplayName,
                        });
                    }
                }
                else if (mediaType == MediaType.Video)
                {
                    if (this.videoSockets == null)
                    {
                        this.GraphLogger.Warn($"video sockets were not created");
                    }
                    else
                    {
                        if (!this.socketUserMapping.ContainsKey((int)socketId))
                        {
                            this.socketUserMapping.Add((int)socketId, participant.Id);
                        }
                        else
                        {
                            this.socketUserMapping[(int)socketId] = participant.Id;
                        }

                        if (!this.userVideoData.ContainsKey(participant.Id))
                        {
                            this.userVideoData.Add(participant.Id, new List<MediaPayload>());
                        }

                        this.userVideoData[participant.Id].Add(new MediaPayload
                        {
                            Data = null,
                            Timestamp = DateTime.UtcNow.Ticks,
                            Width = 0,
                            Height = 0,
                            ColorFormat = VideoColorFormat.H264,
                            FrameRate = 0,
                            Event = "Subscribed",
                            UserId = participant.Id,
                            DisplayName = participant.DisplayName,
                        });
                        this.videoSockets[(int)socketId].Subscribe(videoResolution, mediaSourceId);
                    }
                }
            }
            catch (Exception ex)
            {
                this.GraphLogger.Error(ex, $"Video Subscription failed for the socket: {socketId} and MediaSourceId: {mediaSourceId} with exception");

                this.log += DateTimeOffset.UtcNow.ToString() + $"Video Subscription failed for the socket: {socketId} and MediaSourceId: {mediaSourceId} with exception: {ex.Message}\n";
            }
        }        public void Unsubscribe(MediaType mediaType, uint socketId = 0)
        {
            try
            {
                this.ValidateSubscriptionMediaType(mediaType);

                this.GraphLogger.Info($"Unsubscribing to video for the socket: {socketId} and mediaType: {mediaType}");
                this.log += DateTimeOffset.UtcNow.ToString() + " Unubscribed vbss\n";

                if (mediaType == MediaType.Vbss)
                {
                    this.vbssSocket?.Unsubscribe();
                    this.vbssData.Add(new MediaPayload
                    {
                        Data = null,
                        Timestamp = DateTime.UtcNow.Ticks,
                        Width = 0,
                        Height = 0,
                        ColorFormat = VideoColorFormat.H264,
                        FrameRate = 0,
                        Event = "Unsubscribe"
                    });
                }
                else if (mediaType == MediaType.Video)
                {
                    this.videoSockets[(int)socketId]?.Unsubscribe();

                    this.userVideoData[this.socketUserMapping[(int)socketId]].Add(new MediaPayload
                    {
                        Data = null,
                        Timestamp = DateTime.UtcNow.Ticks,
                        Width = 0,
                        Height = 0,
                        ColorFormat = VideoColorFormat.H264,
                        FrameRate = 0,
                        Event = "Unsubscribe",
                    });
                }
            }
            catch (Exception ex)
            {
                this.GraphLogger.Error(ex, $"Unsubscribing to video failed for the socket: {socketId} with exception");
            }
        }
        private static string MakeValidFileName(string name)
        {
            string invalidChars = System.Text.RegularExpressions.Regex.Escape(new string(System.IO.Path.GetInvalidFileNameChars()));
            string invalidRegStr = string.Format(@"([{0}]*\.+$)|([{0}]+)", invalidChars);

            return System.Text.RegularExpressions.Regex.Replace(name, invalidRegStr, "_");
        }
        private void ValidateSubscriptionMediaType(MediaType mediaType)
        {
            if (mediaType != MediaType.Vbss && mediaType != MediaType.Video)
            {
                throw new ArgumentOutOfRangeException($"Invalid mediaType: {mediaType}");
            }
        }
        private void OnAudioMediaReceived(object sender, AudioMediaReceivedEventArgs e)
        {
             try
            {
                long length = (int)e.Buffer.Length;
                byte[] retrievedBuffer = new byte[length];
                Marshal.Copy(e.Buffer.Data, retrievedBuffer, 0, (int)length);
                //TODO ADD AUDIO PAYLOAD PLS
                this.audioData.Add(new AudioPayload
                {
                    Data = retrievedBuffer,
                    Timestamp = e.Buffer.Timestamp,
                    Length = e.Buffer.Length,
                });
            }
            catch (Exception ex)
            {
                this.log += e.Buffer.Timestamp + $" {ex.Message} {ex.InnerException}";
            }
            finally
            {
                e.Buffer.Dispose();
            }
            #region Old function
            // this.GraphLogger.Info($"Received Audio: [AudioMediaReceivedEventArgs(Data=<{e.Buffer.Data.ToString()}>, Length={e.Buffer.Length}, Timestamp={e.Buffer.Timestamp})]");
            // try
            // {
            //     if (e.Buffer != null && e.Buffer.UnmixedAudioBuffers != null)
            //     {
            //         for (int i = 0; i < e.Buffer.UnmixedAudioBuffers.Length; i++)
            //         {
            //             // Transcribe
            //             var lTrans = this.GetSTTEngine(e.Buffer.UnmixedAudioBuffers[i].ActiveSpeakerId, this.mTranscriptionLanguage, this.mTranslationLanguages);
            //             if (lTrans != null)
            //             {
            //                 lTrans.Transcribe(e.Buffer.UnmixedAudioBuffers[i]);
            //             }
            //         }
            //     }

            //     _mediaStream.AppendAudioBuffer(e.Buffer, this.participants).Wait();
            //     e.Buffer.Dispose();
            // }
            // catch (Exception ex)
            // {
            //     this.GraphLogger.Error(ex);
            // }
            // finally
            // {
            //     e.Buffer.Dispose();
            // }
            #endregion

        }
        private void OnVideoMediaReceived(object sender, VideoMediaReceivedEventArgs e)
        {
            this.GraphLogger.Info($"[{e.SocketId}]: Received Video: [VideoMediaReceivedEventArgs(Data=<{e.Buffer.Data.ToString()}>, Length={e.Buffer.Length}, Timestamp={e.Buffer.Timestamp}, Width={e.Buffer.VideoFormat.Width}, Height={e.Buffer.VideoFormat.Height}, ColorFormat={e.Buffer.VideoFormat.VideoColorFormat}, FrameRate={e.Buffer.VideoFormat.FrameRate})]");
            // this.log += DateTimeOffset.UtcNow.ToString() + $"[{e.SocketId}]: Received Video: [VideoMediaReceivedEventArgs(Data=<{e.Buffer.Data.ToString()}>, Length={e.Buffer.Length}, Timestamp={e.Buffer.Timestamp}, Width={e.Buffer.VideoFormat.Width}, Height={e.Buffer.VideoFormat.Height}, ColorFormat={e.Buffer.VideoFormat.VideoColorFormat}, FrameRate={e.Buffer.VideoFormat.FrameRate})]\n";

            try
            {
                int length = (int)e.Buffer.Length;
                //unsafe
                {
                    // this.log += DateTimeOffset.UtcNow.ToString() + "Creating new byte array\n";
                    byte[] second = new byte[length];
                    // this.log += DateTimeOffset.UtcNow.ToString() + "Copy from pointer to byte array\n";
                    Marshal.Copy(e.Buffer.Data, second, 0, length);

                    this.userVideoData[this.socketUserMapping[e.SocketId]].Add(new MediaPayload
                    {
                        Data = second,
                        Timestamp = e.Buffer.Timestamp,
                        Width = e.Buffer.VideoFormat.Width,
                        Height = e.Buffer.VideoFormat.Height,
                        ColorFormat = e.Buffer.VideoFormat.VideoColorFormat,
                        FrameRate = e.Buffer.VideoFormat.FrameRate,
                    });
                }
            }
            catch (Exception ex)
            {
                this.GraphLogger.Warn("Exception");
                this.log += DateTimeOffset.UtcNow.ToString() + " Failed: " + ex.Message + " " + ex.InnerException + "\n";
            }

            e.Buffer.Dispose();
        }
        private void OnVbssMediaReceived(object sender, VideoMediaReceivedEventArgs e)
        {
            this.GraphLogger.Info($"[{e.SocketId}]: Received VBSS: [VideoMediaReceivedEventArgs(Data=<{e.Buffer.Data.ToString()}>, Length={e.Buffer.Length}, Timestamp={e.Buffer.Timestamp}, Width={e.Buffer.VideoFormat.Width}, Height={e.Buffer.VideoFormat.Height}, ColorFormat={e.Buffer.VideoFormat.VideoColorFormat}, FrameRate={e.Buffer.VideoFormat.FrameRate})]");
            // this.log += DateTimeOffset.UtcNow.ToString() + $"[{e.SocketId}]: Received VBSS: [VideoMediaReceivedEventArgs(Data=<{e.Buffer.Data.ToString()}>, Length={e.Buffer.Length}, Timestamp={e.Buffer.Timestamp}, Width={e.Buffer.VideoFormat.Width}, Height={e.Buffer.VideoFormat.Height}, ColorFormat={e.Buffer.VideoFormat.VideoColorFormat}, FrameRate={e.Buffer.VideoFormat.FrameRate})]\n";
            try
            {
                int length = (int)e.Buffer.Length;

                // this.log += DateTimeOffset.UtcNow.ToString() + "Creating new byte array\n";
                byte[] second = new byte[length];
                // this.log += DateTimeOffset.UtcNow.ToString() + "Copy from pointer to byte array\n";
                Marshal.Copy(e.Buffer.Data, second, 0, length);

                this.vbssData.Add(new MediaPayload
                {
                    Data = second,
                    Timestamp = e.Buffer.Timestamp,
                    Width = e.Buffer.VideoFormat.Width,
                    Height = e.Buffer.VideoFormat.Height,
                    ColorFormat = e.Buffer.VideoFormat.VideoColorFormat,
                    FrameRate = e.Buffer.VideoFormat.FrameRate,
                });

            }
            catch (Exception ex)
            {
                this.GraphLogger.Warn("Exception");
                this.log += DateTimeOffset.UtcNow.ToString() + " Failed: " + ex.Message + " " + ex.InnerException + "\n";
            }

            e.Buffer.Dispose();
        }

        class MeetingInfo
        {
            public string MeetingName { get; set; }
            public string MeetingId { get; set; }
        }
        #endregion
        #region the dumb stuff

        private MySTT GetSTTEngine(uint aUserId, string aTranscriptionLanguage, string[] aTranslationLanguages)
        {
            try
            {
                if (this.mSpeechToTextPool.ContainsKey(aUserId))
                {
                    var lexit = this.mSpeechToTextPool[aUserId];

                    if (!lexit.IsParticipantResolved)
                    {
                        // Try to resolved again
                        var lParticipantInfo = this.TryToResolveParticipant(aUserId);

                        //Update if resolved.
                        if (lParticipantInfo.Item3)
                        {
                            lexit.UpdateParticipant(lParticipantInfo);
                        }
                    }

                    return lexit;
                }
                else
                {
                    var lParticipantInfo = this.TryToResolveParticipant(aUserId);

                    var lNewSE = new MySTT(this._callId.ToString(), aTranscriptionLanguage, aTranslationLanguages, lParticipantInfo.Item1, lParticipantInfo.Item2, lParticipantInfo.Item3, this.GraphLogger, this._eventPublisher, this._settings);
                    this.mSpeechToTextPool.Add(aUserId, lNewSE);

                    return lNewSE;
                }
            }
            catch (Exception ex)
            {
                this.GraphLogger.Error($"GetSTTEngine failed for userid: {aUserId}. Details: {ex.Message}");                
            }

            return null;
        }

        private Tuple<String, String, Boolean> TryToResolveParticipant(uint aUserId)
        {
            bool lIsParticipantResolved = false;
            string lUserDisplayName = aUserId.ToString();
            string lUserId = aUserId.ToString();

            IParticipant participant = this.GetParticipantFromMSI(aUserId);
            var participantDetails = participant?.Resource?.Info?.Identity?.User;

            if (participantDetails != null)
            {
                lUserDisplayName = participantDetails.DisplayName;
                lUserId = participantDetails.Id;
                lIsParticipantResolved = true;
            }

            return new Tuple<string, string, bool>(lUserDisplayName, lUserId, lIsParticipantResolved);
        }

        private IParticipant GetParticipantFromMSI(uint msi)
        {
            return this.participants.SingleOrDefault(x => x.Resource.IsInLobby == false && x.Resource.MediaStreams.Any(y => y.SourceId == msi.ToString()));
        }
        #endregion
    }
}
