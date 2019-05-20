namespace Rocket.Chat.Net.Bot
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Microsoft.Extensions.Logging;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using Rocket.Chat.Net.Bot.Interfaces;
    using Rocket.Chat.Net.Bot.Models;
    using Rocket.Chat.Net.Driver;
    using Rocket.Chat.Net.Interfaces;
    using Rocket.Chat.Net.Models;

    public abstract class RocketChatBot : IDisposable
    {
        protected readonly ILogger logger;
        private readonly List<IBotResponse> _botResponses = new List<IBotResponse>();

        public IRocketChatDriver Driver { get; }

        public string LoginToken { get; private set; }
        public string UserId { get; private set; }

        public RocketChatBot(IRocketChatDriver driver, ILogger logger)
        {
            Driver = driver;
            this.logger = logger;

            Driver.MessageReceived += DriverOnMessageReceived;
            Driver.DdpReconnect += DriverOnDdpReconnect;
            Driver.CollectionChanged += SubscribedCollectionChanged;
        }

        public RocketChatBot(string url, bool useSsl, ILogger logger = null)
            : this(new RocketChatDriver(url, useSsl, logger), logger)
        {
        }

        public async Task ConnectAsync()
        {
            await Driver.ConnectAsync().ConfigureAwait(false);
            logger.LogInformation("Successfully connected bot");
        }

        public async Task LoginAsync(ILoginOption loginOption)
        {
            logger.LogInformation("Logging-in.");
            Net.Models.MethodResults.MethodResult<Net.Models.MethodResults.LoginResult> result;
            try
            {
                result = await Driver.LoginAsync(loginOption).ConfigureAwait(false);

                if (result.HasError)
                {
                    throw new Exception($"Login failed: {result.Error.Message}.");
                }

                LoginToken = result.Result.Token;
                UserId = result.Result.UserId;
            }
            catch (TaskCanceledException)
            {
                logger.LogError("Login failed, retrying");
                await LoginAsync(loginOption);
            }
        }

        /// <summary>
        /// Returns the event name
        /// </summary>
        /// <returns></returns>
        public async Task SubscribeAsync(string stream, string @event, params object[] parameters)
        {
            if (UserId == null)
                throw new InvalidOperationException("No user ID available. Log in first.");

            await Driver.SubscribeToAsync(stream, @event, false);
        }

        private readonly Func<IResponse> responseFactory = () => null;

        protected async void SubscribedCollectionChanged(string streamName, JObject fields)
        {
            StreamMessage msg = fields.ToObject<StreamMessage>();
            await ProcessRequest(streamName + "/" + msg.EventName, msg.Args);
        }

        protected abstract Task ProcessRequest(string name, List<JObject> messageArgs);

        private async void RespondAsync(IMessageResponse response) => await SendMessageAsync(response);

        public async Task ResumeAsync()
        {
            if (LoginToken == null)
            {
                throw new InvalidOperationException("Must have logged in first.");
            }

            logger.LogInformation($"Resuming session {LoginToken}.");
            var result = await Driver.LoginResumeAsync(LoginToken).ConfigureAwait(false);
            if (result.HasError)
            {
                throw new Exception($"Resume failed: {result.Error.Message}.");
            }

            LoginToken = result.Result.Token;
        }

        public async Task LogoutOtherClientsAsync()
        {
            if (LoginToken == null)
            {
                throw new InvalidOperationException("Must have logged in first.");
            }

            logger.LogInformation($"Getting new token {LoginToken}.");
            var newToken = await Driver.GetNewTokenAsync().ConfigureAwait(false);
            if (newToken.HasError)
            {
                throw new Exception($"Resume failed: {newToken.Error.Message}.");
            }

            logger.LogInformation($"Logging out all other users {LoginToken}.");
            var result = await Driver.RemoveOtherTokensAsync().ConfigureAwait(false);
            if (result.HasError)
            {
                throw new Exception($"Resume failed: {result.Error.Message}.");
            }

            LoginToken = newToken.Result.Token;
        }

        public void AddResponse(IBotResponse botResponse)
        {
            logger.LogInformation($"Added response {botResponse.GetType()}.");
            _botResponses.Add(botResponse);
        }

        private void DriverOnMessageReceived(RocketMessage rocketMessage)
        {
            var context = new ResponseContext
            {
                Message = rocketMessage,
                BotHasResponded = false,
                BotUserId = Driver.UserId,
                BotUserName = Driver.Username
            };

            Task.Run(async () => // async this to prevent holding up the message loop, I will handle exceptions.
            {
                foreach (var botResponse in GetValidResponses(context, _botResponses))
                {
                    try
                    {
                        logger.LogDebug("Trying response {responsetype}.", botResponse.GetType());
                        var hasResponse = false;
                        foreach (var response in botResponse.GetResponse(context, this))
                        {
                            hasResponse = true;
                            await SendMessageAsync(response).ConfigureAwait(false);
                        }

                        if (hasResponse)
                        {
                            logger.LogDebug("Response succeeded.");
                        }
                    }
                    catch (Exception e)
                    {
                        logger.LogInformation(null, e, "ERROR");
                    }
                }
            });
        }

        protected async Task SendMessageAsync(IMessageResponse response)
        {
            var attachmentMessage = response as AttachmentResponse;
            var basicMessage = response as BasicResponse;

            if (attachmentMessage != null)
            {
                await Driver.SendCustomMessageAsync(attachmentMessage.Attachment, attachmentMessage.RoomId).ConfigureAwait(false);
            }
            else if (basicMessage != null)
            {
                await Driver.SendMessageAsync(basicMessage.Message, basicMessage.RoomId).ConfigureAwait(false);
            }
            else
            {
                throw new InvalidOperationException(
                    $"The result of {nameof(IBotResponse.GetResponse)} is either null or not of a supported type.");
            }
        }

        private IEnumerable<IBotResponse> GetValidResponses(ResponseContext context, IEnumerable<IBotResponse> possibleResponses)
        {
            foreach (var response in possibleResponses)
            {
                var canRespond = response.CanRespond(context);
                if (canRespond)
                {
                    context.BotHasResponded = true;
                    yield return response;
                }
            }
        }

        private void DriverOnDdpReconnect()
        {
            logger.LogInformation("Reconnect requested...");
            if (LoginToken != null)
            {
                ResumeAsync().Wait();
            }
        }

        public void Dispose()
        {
            Driver.Dispose();
        }
    }

    public class StreamMessage
    {
        public string EventName { get; set; }
        public List<JObject> Args { get; set; }
    }

    public class NotifyUserMessageArgument
    {
        public const string PRIVATEMESSAGETYPE = "d";
        public const string PUBLICCHANNELMESSAGETYPE = "c";
        public const string PRIVATECHANNELMESSAGE = "p";
        public bool IsDirectMessage => Payload.Type == PRIVATEMESSAGETYPE;
        public bool IsPublicChannelMessage => Payload.Type == PUBLICCHANNELMESSAGETYPE;
        public bool IsPrivateChannelMessage => Payload.Type == PRIVATECHANNELMESSAGE;
        public bool IsChannelMessage => IsPrivateChannelMessage || IsPublicChannelMessage;
        public string Title { get; set; }
        public string Text { get; set; }
        public Payload Payload { get; set; }
    }

    public class Payload
    {
        [JsonProperty("_id")]
        public string ID { get; set; }
        [JsonProperty("Rid")]
        public string RoomId { get; set; }
        public Sender Sender { get; set; }
        public string Type { get; set; }
        public Message Message { get; set; }
    }

    public class Sender
    {
        [JsonProperty("_id")]
        public string ID { get; set; }
        public string Username { get; set; }
        public string Name { get; set; }
    }

    public class Message
    {
        public string Msg { get; set; }
    }

    public interface IResponse
    {
        IMessageResponse RespondTo(IEnumerable<JObject> input);
    }
    public abstract class Response<T> : IResponse where T : class, new()
    {
        protected abstract IMessageResponse RespondTo(T input);

        IMessageResponse IResponse.RespondTo(IEnumerable<JObject> input) => RespondTo(input.First().ToObject<T>());
    }
}