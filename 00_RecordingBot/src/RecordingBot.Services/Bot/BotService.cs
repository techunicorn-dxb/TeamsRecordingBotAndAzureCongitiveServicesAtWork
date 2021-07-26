// ***********************************************************************
// Assembly         : RecordingBot.Services
// Author           : JasonTheDeveloper
// Created          : 09-07-2020
//
// Last Modified By : dannygar
// Last Modified On : 09-03-2020
// ***********************************************************************
// <copyright file="BotService.cs" company="Microsoft">
//     Copyright ©  2020
// </copyright>
// <summary></summary>
// ***********************************************************************
using Microsoft.Graph;
using Microsoft.Graph.Communications.Calls;
using Microsoft.Graph.Communications.Calls.Media;
using Microsoft.Graph.Communications.Client;
using Microsoft.Graph.Communications.Common;
using Microsoft.Graph.Communications.Common.Telemetry;
using Microsoft.Graph.Communications.Resources;
using Microsoft.Skype.Bots.Media;
using RecordingBot.Model.Models;
using RecordingBot.Services.Authentication;
using RecordingBot.Services.Contract;
using RecordingBot.Services.ServiceSetup;
using RecordingBot.Services.Util;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
// using Sample.Common.Logging;

namespace RecordingBot.Services.Bot
{
    /// <summary>
    /// Class BotService.
    /// Implements the <see cref="System.IDisposable" />
    /// Implements the <see cref="RecordingBot.Services.Contract.IBotService" />
    /// </summary>
    /// <seealso cref="System.IDisposable" />
    /// <seealso cref="RecordingBot.Services.Contract.IBotService" />
    public class BotService : IDisposable 
    {
        //IBotService
        /// <summary>
        /// The logger
        /// </summary>
        public static BotService Instance { get; } = new BotService();
        private IGraphLogger _logger;
        /// <summary>
        /// The event publisher
        /// </summary>
        private IEventPublisher _eventPublisher;
        /// <summary>
        /// The settings
        /// </summary>
        private AzureSettings _settings;

        /// <summary>
        /// Gets the collection of call handlers.
        /// </summary>
        /// <value>The call handlers.</value>
        public ConcurrentDictionary<string, CallHandler> CallHandlers { get; } = new ConcurrentDictionary<string, CallHandler>();

        /// <summary>
        /// Gets the entry point for stateful bot.
        /// </summary>
        /// <value>The client.</value>
        public ICommunicationsClient Client { get; private set; }
        //new
        private IConfiguration configuration;
        
        // public SampleObserver Observer { get; private set; }
        
        private Dictionary<string, JoinCallBody> mCallLanguagesDict = new Dictionary<string, JoinCallBody>();


        /// <inheritdoc />
        public void Dispose()
        {
            this.Client?.Dispose();
            this.Client = null;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="BotService" /> class.

        /// </summary>
        /// <param name="logger">The logger.</param>
        /// <param name="eventPublisher">The event publisher.</param>
        /// <param name="settings">The settings.</param>
        // public BotService(
        //     IGraphLogger logger,            
        //     IEventPublisher eventPublisher,
        //     IAzureSettings settings

        // )
        // {
        //     _logger = logger;
        //     _eventPublisher = eventPublisher;
        //     _settings = (AzureSettings)settings;
        // }

        /// <summary>
        /// Initialize the instance.
        /// </summary>
        public void Initialize(Service service, IGraphLogger logger)
        {
            this.configuration = service.Configuration;
            Validator.IsNull(this._logger,"Multiple initializations are not allowed.");
            this._logger = logger;
                        var name = this.GetType().Assembly.GetName().Name;
            var builder = new CommunicationsClientBuilder(
                name,
                service.Configuration.AadAppId,
                this._logger);

            var authProvider = new AuthenticationProvider(
                name,
                service.Configuration.AadAppId,
                service.Configuration.AadAppSecret,
                this._logger);

            builder.SetAuthenticationProvider(authProvider);

            // [LOCAL UPDATE]
            builder.SetNotificationUrl(service.Configuration.CallControlBaseUrl);
            // builder.SetNotificationUrl( new Uri("https://f9e560cb4b6b.ngrok.io/api/calling/notification"));

            builder.SetMediaPlatformSettings(service.Configuration.MediaPlatformSettings);

            builder.SetServiceBaseUrl(service.Configuration.PlaceCallEndpointUrl);

            this.Client = builder.Build();
            this.Client.Calls().OnIncoming += this.CallsOnIncoming;
            this.Client.Calls().OnUpdated += this.CallsOnUpdated;

         #region oldd

            // var name = this.GetType().Assembly.GetName().Name;
            // var builder = new CommunicationsClientBuilder(
            //     name,
            //     _settings.AadAppId,
            //     _logger);

            // var authProvider = new AuthenticationProvider(
            //     name,
            //     _settings.AadAppId,
            //     _settings.AadAppSecret,
            //     _logger);

            // builder.SetAuthenticationProvider(authProvider);
            // builder.SetNotificationUrl(_settings.CallControlBaseUrl);
            // builder.SetMediaPlatformSettings(_settings.MediaPlatformSettings);
            // builder.SetServiceBaseUrl(_settings.PlaceCallEndpointUrl);

            // this.Client = builder.Build();
            // this.Client.Calls().OnIncoming += this.CallsOnIncoming;
            // this.Client.Calls().OnUpdated += this.CallsOnUpdated;
        
        #endregion
        }

        /// <summary>
        /// End a particular call.
        /// </summary>
        /// <param name="callLegId">The call leg id.</param>
        /// <returns>The <see cref="Task" />.</returns>
        public async Task EndCallByCallLegIdAsync(string callLegId)
        {
            try
            {
                await this.GetHandlerOrThrow(callLegId).Call.DeleteAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Manually remove the call from SDK state.
                // This will trigger the ICallCollection.OnUpdated event with the removed resource.
                this.Client.Calls().TryForceRemove(callLegId, out ICall _);
            }
        }

        /// <summary>
        /// Joins the call asynchronously.
        /// </summary>
        /// <param name="joinCallBody">The join call body.</param>
        /// <returns>The <see cref="ICall" /> that was requested to join.</returns>
        public async Task<ICall> JoinCallAsync(JoinCallBody joinCallBody)
        {
            // A tracking id for logging purposes. Helps identify this call in logs.
            var scenarioId = Guid.NewGuid();

            var (chatInfo, meetingInfo) = JoinInfo.ParseJoinURL(joinCallBody.JoinURL);

            var tenantId = (meetingInfo as OrganizerMeetingInfo).Organizer.GetPrimaryIdentity().GetTenantId();
            var mediaSession = this.CreateLocalMediaSession();

            var joinParams = new JoinMeetingParameters(chatInfo, meetingInfo, mediaSession)
            {
                TenantId = tenantId,
            };

            if (!string.IsNullOrWhiteSpace(joinCallBody.DisplayName))
            {
                // Teams client does not allow changing of ones own display name.
                // If display name is specified, we join as anonymous (guest) user
                // with the specified display name.  This will put bot into lobby
                // unless lobby bypass is disabled.
                joinParams.GuestIdentity = new Identity
                {
                    Id = Guid.NewGuid().ToString(),
                    DisplayName = joinCallBody.DisplayName,
                };
            }

            mCallLanguagesDict.Add(scenarioId.ToString(), joinCallBody);

            var statefulCall = await this.Client.Calls().AddAsync(joinParams, scenarioId).ConfigureAwait(false);


            statefulCall.GraphLogger.Info($"Call creation complete: {statefulCall.Id}");
            return statefulCall;
        }

        /// <summary>
        /// Creates the local media session.
        /// </summary>
        /// <param name="mediaSessionId">The media session identifier.
        /// This should be a unique value for each call.</param>
        /// <returns>The <see cref="ILocalMediaSession" />.</returns>
        private ILocalMediaSession CreateLocalMediaSession(Guid mediaSessionId = default)
        {
             var videoSocketSettings = new List<VideoSocketSettings>();

            // create the receive only sockets settings for the multiview support
            for (int i = 0; i < recorderConstants.NumberOfMultiviewSockets; i++)
            {
                videoSocketSettings.Add(new VideoSocketSettings
                {
                    StreamDirections = StreamDirection.Recvonly,
                    ReceiveColorFormat = VideoColorFormat.H264,
                });
            }

            // Create the VBSS socket settings
            var vbssSocketSettings = new VideoSocketSettings
            {
                StreamDirections = StreamDirection.Recvonly,
                ReceiveColorFormat = VideoColorFormat.H264,
                MediaType = MediaType.Vbss,
                SupportedSendVideoFormats = new List<VideoFormat>
                {
                    // fps 1.875 is required for h264 in vbss scenario.
                    VideoFormat.H264_1920x1080_1_875Fps,
                },
            };

            // create media session object, this is needed to establish call connections
            var mediaSession = this.Client.CreateMediaSession(
                new AudioSocketSettings
                {
                    StreamDirections = StreamDirection.Recvonly,
                    SupportedAudioFormat = AudioFormat.Pcm16K,
                },
                videoSocketSettings,
                vbssSocketSettings,
                mediaSessionId: mediaSessionId);
            return mediaSession;
         #region old
            // try
            // {
            //     // create media session object, this is needed to establish call connections
            //     return this.Client.CreateMediaSession(
            //         new AudioSocketSettings
            //         {
            //             StreamDirections = StreamDirection.Recvonly,
            //             // Note! Currently, the only audio format supported when receiving unmixed audio is Pcm16K
            //             SupportedAudioFormat = AudioFormat.Pcm16K,
            //             ReceiveUnmixedMeetingAudio = true //get the extra buffers for the speakers
            //     },
            //         new VideoSocketSettings
            //         {
            //             StreamDirections = StreamDirection.Inactive
            //         },
            //         mediaSessionId: mediaSessionId);
            // }
            // catch (Exception e)
            // {
            //     _logger.Log(System.Diagnostics.TraceLevel.Error, e.Message);
            //     throw;
            // }
        #endregion
        }

        /// <summary>
        /// Incoming call handler.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="args">The <see cref="CollectionEventArgs{TResource}" /> instance containing the event data.</param>
        private void CallsOnIncoming(ICallCollection sender, CollectionEventArgs<ICall> args)
        {
            
            Guid callId = new Guid();
            args.AddedResources.ForEach(call =>
            {
                try
                {
                    // Get the policy recording parameters.

                    // The context associated with the incoming call.
                    IncomingContext incomingContext =
                        call.Resource.IncomingContext;

                    // The RP participant.
                    string observedParticipantId =
                            incomingContext.ObservedParticipantId;

                    // If the observed participant is a delegate.
                    IdentitySet onBehalfOfIdentity =
                            incomingContext.OnBehalfOf;

                        // If a transfer occured, the transferor.
                    IdentitySet transferorIdentity =
                            incomingContext.Transferor;

                    string countryCode = null;
                    EndpointType? endpointType = null;

                    // Note: this should always be true for CR calls.
                    if (incomingContext.ObservedParticipantId == incomingContext.SourceParticipantId)
                    {
                        // The dynamic location of the RP.
                        countryCode = call.Resource.Source.CountryCode;

                        // The type of endpoint being used.
                        endpointType = call.Resource.Source.EndpointType;
                    }

                    IMediaSession mediaSession = Guid.TryParse(call.Id, out callId)
                        ? this.CreateLocalMediaSession(callId)
                        : this.CreateLocalMediaSession();
                    // Answer call
                    call?.AnswerAsync(mediaSession).ForgetAndLogExceptionAsync(
                    call.GraphLogger,
                    $"Answering call {call.Id} with scenario {call.ScenarioId}.");
                }
                catch (Exception e)
                {
                    var innerMessage = e.InnerException == null ? string.Empty : e.InnerException.Message;
                    var innerStack = e.InnerException == null ? string.Empty : e.InnerException.StackTrace;

                    this._logger.Log(
                        System.Diagnostics.TraceLevel.Error,
                        $"type: {e.GetType()}\nmessage: {e.Message}\ntrace: {e.StackTrace}\ninner message: {innerMessage}\ntrace: {innerStack}",
                        memberName: VLobbyLogConstants.VLobbyLogError,
                        properties: new List<object> { new CallData { CallId = callId, MeetingId = call.Resource.ChatInfo.ThreadId } });

                    throw e;
                }
            });

            #region old
            // args.AddedResources.ForEach(call =>
            // {
            //     // Get the policy recording parameters.

            //     // The context associated with the incoming call.
            //     IncomingContext incomingContext =
            //         call.Resource.IncomingContext;                

            //     // The RP participant.
            //     string observedParticipantId =
            //         incomingContext.ObservedParticipantId;

            //     // If the observed participant is a delegate.
            //     IdentitySet onBehalfOfIdentity =
            //         incomingContext.OnBehalfOf;

            //     // If a transfer occured, the transferor.
            //     IdentitySet transferorIdentity =
            //         incomingContext.Transferor;

            //     string countryCode = null;
            //     EndpointType? endpointType = null;

            //     // Note: this should always be true for CR calls.
            //     if (incomingContext.ObservedParticipantId == incomingContext.SourceParticipantId)
            //     {
            //         // The dynamic location of the RP.
            //         countryCode = call.Resource.Source.CountryCode;

            //         // The type of endpoint being used.
            //         endpointType = call.Resource.Source.EndpointType;
            //     }

            //     IMediaSession mediaSession = Guid.TryParse(call.Id, out Guid callId)
            //         ? this.CreateLocalMediaSession(callId)
            //         : this.CreateLocalMediaSession();

            //     // Answer call
            //     call?.AnswerAsync(mediaSession).ForgetAndLogExceptionAsync(
            //         call.GraphLogger,
            //         $"Answering call {call.Id} with scenario {call.ScenarioId}.");
            // });
            #endregion
        }

        /// <summary>
        /// Updated call handler.
        /// </summary>
        /// <param name="sender">The <see cref="ICallCollection" /> sender.</param>
        /// <param name="args">The <see cref="CollectionEventArgs{ICall}" /> instance containing the event data.</param>
        private void CallsOnUpdated(ICallCollection sender, CollectionEventArgs<ICall> args)
        {
            Guid callId = new Guid();
            foreach (var call in args.AddedResources)
            {
                try
                {
                    if (call.Resource.ChatInfo != null)
                    {
                        if (!Guid.TryParse(call.Id, out callId))
                        {
                            callId = new Guid();
                        }
                        //VirtualLobbyRepository repository = new VirtualLobbyRepository(this.configuration.VirtualLobbyApiDomain, this.configuration.VirtualLobbyApiKey);
                        //var task = repository.LogRecordingStartAsync(callId, call.Resource.ChatInfo.ThreadId);
                        //task.Wait();
                        //var isSuccess = task.Result;
                        var isSuccess = true;
                        if (isSuccess && call.Resource.ChatInfo != null && call.Resource.ChatInfo.ThreadId != null)
                        {
                            var callHandler = new CallHandler(call, call.Resource.ChatInfo.ThreadId, this.configuration);
                            this.CallHandlers[call.Id] = callHandler;
                        }
                    }
                }
                catch (Exception e)
                {
                    var innerMessage = e.InnerException == null ? string.Empty : e.InnerException.Message;
                    var innerStack = e.InnerException == null ? string.Empty : e.InnerException.StackTrace;

                    this._logger.Log(
                        System.Diagnostics.TraceLevel.Error,
                        $"type: {e.GetType()}\nmessage: {e.Message}\ntrace: {e.StackTrace}\ninner message: {innerMessage}\ntrace: {innerStack}",
                        memberName: VLobbyLogConstants.VLobbyLogError,
                        properties: new List<object> { new CallData { CallId = callId, MeetingId = call.Resource.ChatInfo.ThreadId } });

                    throw e;
                }
            }

            foreach (var call in args.RemovedResources)
            {
                try
                {
                    if (!Guid.TryParse(call.Id, out callId))
                    {
                        callId = new Guid();
                    }

                    if (this.CallHandlers.TryRemove(call.Id, out CallHandler handler))
                    {
                        handler.Dispose();
                    }
                }
                catch (Exception e)
                {
                    var innerMessage = e.InnerException == null ? string.Empty : e.InnerException.Message;
                    var innerStack = e.InnerException == null ? string.Empty : e.InnerException.StackTrace;

                    this._logger.Log(
                        System.Diagnostics.TraceLevel.Error,
                        $"type: {e.GetType()}\nmessage: {e.Message}\ntrace: {e.StackTrace}\ninner message: {innerMessage}\ntrace: {innerStack}",
                        memberName: VLobbyLogConstants.VLobbyLogError,
                        properties: new List<object> { new CallData { CallId = callId, MeetingId = call.Resource.ChatInfo.ThreadId } });

                    throw e;
                }
            }
                #region old Version
            // foreach (var call in args.AddedResources)
            // {
                // Use this in order to get the default values for languages.
            //     JoinCallBody lJoinBody = new JoinCallBody();
                
            //     if (call.ScenarioId != null && mCallLanguagesDict.ContainsKey(call.ScenarioId.ToString()))
            //     {
            //         lJoinBody = mCallLanguagesDict[call.ScenarioId.ToString()] as JoinCallBody;

            //         _eventPublisher.Publish("CallsOnUpdated", $"JoinBody found -> Settings languages: {lJoinBody.TranscriptionLanguage}, {lJoinBody.TranslationLanguages}");
            //     }
            //     else
            //     {
            //         _eventPublisher.Publish("CallsOnUpdated", $"No JoinBody object found -> Settings default languages: {lJoinBody.TranscriptionLanguage}, {lJoinBody.TranslationLanguages}");
            //     }

            //     var callHandler = new CallHandler(call, lJoinBody.TranscriptionLanguage, lJoinBody.TranslationLanguages, _settings, _eventPublisher);
            //     this.CallHandlers[call.Id] = callHandler;
            // }

            // foreach (var call in args.RemovedResources)
            // {
            //     if (this.CallHandlers.TryRemove(call.Id, out CallHandler handler))
            //     {
            //         handler.Dispose();
            //     }
            // }
            #endregion
        }

        /// <summary>
        /// The get handler or throw.
        /// </summary>
        /// <param name="callLegId">The call leg id.</param>
        /// <returns>The <see cref="CallHandler" />.</returns>
        /// <exception cref="ArgumentException">call ({callLegId}) not found</exception>
        private CallHandler GetHandlerOrThrow(string callLegId)
        {
            if (!this.CallHandlers.TryGetValue(callLegId, out CallHandler handler))
            {
                throw new ArgumentException($"call ({callLegId}) not found");
            }

            return handler;
        }
        //Stuff directly from compliance
        private System.Net.Http.HttpResponseMessage GetFolderName(string threadId)
        {
            System.Net.Http.HttpClient httpClient = new System.Net.Http.HttpClient();
            var requestMessage = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, $"{this.configuration.NameGetUrl}/api/name?threadId={threadId}");
            requestMessage.Headers.Add("X-Api-Key", this.configuration.NameGetApiKey);
            var getTask = httpClient.SendAsync(requestMessage);
            getTask.Wait();
            return getTask.Result;
        }
    }
}
