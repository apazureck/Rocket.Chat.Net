using Newtonsoft.Json;
using System.Text;

namespace Rocket.Chat.Net.Driver
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;

    using JetBrains.Annotations;
    using Microsoft.Extensions.Logging;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    using Rocket.Chat.Net.Collections;
    using Rocket.Chat.Net.Helpers;
    using Rocket.Chat.Net.Interfaces;
    using Rocket.Chat.Net.Models;
    using Rocket.Chat.Net.Models.Collections;
    using Rocket.Chat.Net.Models.LoginOptions;
    using Rocket.Chat.Net.Models.MethodResults;
    using Rocket.Chat.Net.Models.SubscriptionResults;
    using Rocket.Chat.Net.Standard.Models.RestApi.Responses;

    public class RocketChatDriver : IRocketChatDriver
    {
        private const string MessageTopic = "stream-messages";
        private const int MessageSubscriptionLimit = 10;

        private readonly IStreamCollectionDatabase _collectionDatabase;
        private readonly ILogger _logger;

        public string PlainUrl { get; }

        private readonly IDdpClient _client;

        public event MessageReceived MessageReceived;
        public event DdpReconnect DdpReconnect;

        private CancellationToken TimeoutToken => CreateTimeoutToken();

        public string UserId { get; private set; }
        public string Username { get; private set; }
        private string Password { get; set; }
        public string ServerUrl => _client.Url;
        public bool IsBot { get; set; }

        public RoomCollection Rooms => new RoomCollection(GetRoomsCollection(), GetRoomInfoCollection());

        public JsonSerializerSettings JsonSerializerSettings { get; private set; }
        public JsonSerializer JsonSerializer => JsonSerializer.Create(JsonSerializerSettings);

        public RocketChatDriver(string url, bool useSsl, ILogger logger = null, bool isBot = true, JsonSerializerSettings jsonSerializerSettings = null)
        {
            IsBot = isBot;
            _logger = logger;
            _collectionDatabase = new StreamCollectionDatabase();

            _logger.LogInformation("Creating client...");
            PlainUrl = url;
            _client = new DdpClient(url, useSsl, _logger);
            _client.DataReceivedRaw += ClientOnDataReceivedRaw;
            _client.DdpReconnect += OnDdpReconnect;
            SetJsonOptions(jsonSerializerSettings);
        }

        public RocketChatDriver(ILogger logger, IDdpClient client, IStreamCollectionDatabase collectionDatabaseDatabase, bool isBot = true,
                                JsonSerializerSettings jsonSerializerSettings = null)
        {
            IsBot = isBot;
            _logger = logger;
            _client = client;
            _collectionDatabase = collectionDatabaseDatabase;
            _client.DataReceivedRaw += ClientOnDataReceivedRaw;
            _client.DdpReconnect += OnDdpReconnect;
            SetJsonOptions(jsonSerializerSettings);
        }

        private void SetJsonOptions(JsonSerializerSettings jsonSerializerSettings = null)
        {
            JsonSerializerSettings = jsonSerializerSettings ?? new JsonSerializerSettings
            {
                Error = (sender, args) =>
                {
                    _logger.LogError("Handled error on (de)serialization. Please report this error to the developer: " + args.ErrorContext.Error.ToString());
                    args.ErrorContext.Handled = true;
                }
            };
        }

        private void ClientOnDataReceivedRaw(string type, JObject data)
        {
            HandleStreamingCollections(type, data);
            HandleRocketMessage(type, data);
        }

        private void HandleStreamingCollections(string type, JObject data)
        {
            var collectionResult = data.ToObject<CollectionResult>(JsonSerializer);
            if (collectionResult.Name == null)
            {
                return;
            }

            var collection = _collectionDatabase.GetOrAddCollection(collectionResult.Name);

            switch (type)
            {
                case "added":
                    collection.Added(collectionResult.Id, collectionResult.Fields);
                    break;
                case "changed":
                    collection.Changed(collectionResult.Id, collectionResult.Fields);
                    RaiseCollectionChangedEvent(collectionResult.Name, collectionResult.Fields);
                    break;
                case "removed":
                    collection.Removed(collectionResult.Id);
                    break;
                default:
                    throw new InvalidOperationException($"Encountered a unknown subscription update type {type}.");
            }
        }

        bool isRestApiLoggedIn = false;
        string HttpUserId;
        string RestAuthToken;

        public async Task<bool> CheckLogin(string username, string password)
        {
            using (var http = new HttpClient())
            {
                var response = await http.PostAsync("http://" + PlainUrl + "/api/v1/login", new JsonContent(new
                {
                    user = username,
                    password
                }));

                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException("Could not login user");
                var resstr = await response.Content.ReadAsStringAsync();
                var loginData = JsonConvert.DeserializeObject<RocketChatRestResponse<LoginResponseData>>(resstr);
                return loginData.Status == "success";
            }
        }

        private async Task LogInToRestApi()
        {
            if (isRestApiLoggedIn)
                return;

            using (var http = new HttpClient())
            {
                var response = await http.PostAsync("http://" + PlainUrl + "/api/v1/login", new JsonContent(new
                {
                    user = Username,
                    password = Password
                }));

                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException("Could not login user");
                var resstr = await response.Content.ReadAsStringAsync();
                var loginData = JsonConvert.DeserializeObject<RocketChatRestResponse<LoginResponseData>>(resstr);
                if (loginData.Status != "success")
                    throw new InvalidOperationException("Could not log in");

                HttpUserId = loginData.Data.UserId;
                RestAuthToken = loginData.Data.AuthToken;

                isRestApiLoggedIn = true;
            }
        }

        public async Task<List<FullUser>> GetUserList()
        {
            if (_client.SessionId == null)
                throw new InvalidOperationException("Driver is not logged in");

            await LogInToRestApi();

            using (HttpClient http = new HttpClient())
            {
                var request = new HttpRequestMessage(HttpMethod.Get, "http://" + PlainUrl + "/api/v1/users.list");
                request.Headers.Add("X-Auth-Token", RestAuthToken);
                request.Headers.Add("X-User-Id", HttpUserId);
                HttpResponseMessage response = await http.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException("Could not request User List");

                string rs = await response.Content.ReadAsStringAsync();
                return JsonConvert.DeserializeObject<UserResponse>(rs).Users;
            }
        }

        private class UserResponse {
            public List<FullUser> Users { get; set; }
            public int Count { get; set; }
            public int Offset { get; set; }
            public int Total { get; set; }
            public bool Success { get; set; }
        }

        public event Action<string, JObject> CollectionChanged;
        protected void RaiseCollectionChangedEvent(string name, JObject fields)
        {
            CollectionChanged?.Invoke(name, fields);
        }

        private void HandleRocketMessage(string type, JObject data)
        {
            var o = data.ToObject<SubscriptionResult<JObject>>(JsonSerializer);
            var isMessage = type == "added" && o.Collection == MessageTopic && o.Fields["args"] != null;
            if (!isMessage)
            {
                return;
            }

            var messageRaw = o.Fields["args"][1];
            var message = messageRaw.ToObject<RocketMessage>(JsonSerializer);
            message.IsBotMentioned = message.Mentions.Any(x => x.Id == UserId);
            message.IsFromMyself = message.CreatedBy.Id == UserId;

            var rooms = Rooms;

            message.Room = rooms.FirstOrDefault(x => x.Id == message.RoomId);

            var edit = message.WasEdited ? "(EDIT)" : "";
            var mentioned = message.IsBotMentioned ? "(Mentioned)" : "";
            _logger.LogInformation(
                $"Message from {message.CreatedBy.Username}@{message.RoomId}{edit}{mentioned}: {message.Message}");

            OnMessageReceived(message);
        }

        public async Task ConnectAsync()
        {
            _logger.LogInformation($"Connecting client to {_client.Url}...");
            await _client.ConnectAsync(TimeoutToken).ConfigureAwait(false);
        }

        public async Task<MethodResult<CreateRoomResult>> CreateGroupAsync(string groupName, IList<string> members = null)
        {
            var results =
                await _client.CallAsync("createPrivateGroup", TimeoutToken, groupName, members ?? new List<string>()).ConfigureAwait(false);

            return results.ToObject<MethodResult<CreateRoomResult>>(JsonSerializer);
        }

        public async Task SubscribeToRoomListAsync()
        {
            await _client.SubscribeAndWaitAsync("subscription", TimeoutToken).ConfigureAwait(false);
            var roomCollection = GetRoomsCollection();
            if (roomCollection == null)
            {
                _logger.LogError("RoomCollection should not be null.");
                return;
            }
            roomCollection.Modified += async (sender, args) =>
            {
                if (args.ModificationType == ModificationType.Added)
                {
                    var room = args.Result;
                    await SubscribeToRoomInformationAsync(room.Name, room.Type).ConfigureAwait(false);
                }
            };
            foreach (var room in roomCollection.Items().ToList().Select(x => x.Value))
            {
                await SubscribeToRoomInformationAsync(room.Name, room.Type).ConfigureAwait(false);
            }
        }

        public async Task<JObject> GetPermissionsAsync()
        {
            return await _client.CallAsync("permissions/get", TimeoutToken);
        }

        public async Task SubscribeToRoomAsync(string roomId = null)
        {
            _logger.LogInformation($"Subscribing to Room: #{roomId ?? "ALLROOMS"}");
            var result = await _client.CallAsync("rooms/get", TimeoutToken, JObject.Parse("{ \"$date\": 0 }"));
            await _client.SubscribeAsync("", TimeoutToken, roomId, MessageSubscriptionLimit.ToString()).ConfigureAwait(false);
        }

        public async Task SubscribeToRoomInformationAsync(string roomName, RoomType type)
        {
            await _client.SubscribeAndWaitAsync("room", TimeoutToken, $"{(char) type}{roomName}").ConfigureAwait(false);
        }

        public async Task SubscribeToFilteredUsersAsync(string username = "")
        {
            _logger.LogInformation($"Subscribing to filtered users searching for {username ?? "ANY"}.");
            await _client.SubscribeAsync("userData", TimeoutToken).ConfigureAwait(false);
        }

        public async Task SubscribeToAsync(string streamName, params object[] o)
        {
            await _client.SubscribeAsync(streamName, TimeoutToken, o).ConfigureAwait(false);
        }

        public async Task<FullUser> GetFullUserDataAsync(string username)
        {
            await _client.SubscribeAndWaitAsync("fullUserData", TimeoutToken, username, 1).ConfigureAwait(false);

            var success = _collectionDatabase.TryGetCollection("users", out IStreamCollection data);
            if (!success)
            {
                return null;
            }

            KeyValuePair<string, FullUser> userPair = data
                .Items<FullUser>()
                .FirstOrDefault(x => x.Value.Username == username);
            FullUser user = userPair.Value;
            if (user != null)
            {
                user.Id = userPair.Key;
            }
            return user;
        }

        public async Task PingAsync()
        {
            _logger.LogInformation("Pinging server.");
            await _client.PingAsync(TimeoutToken).ConfigureAwait(false);
        }

        public async Task<MethodResult<LoginResult>> LoginAsync(ILoginOption loginOption)
        {
            if (loginOption is LdapLoginOption ldapLogin)
            {
                Username = ldapLogin.Username;
                Password = ldapLogin.Password;
                var res = await LoginWithLdapAsync(ldapLogin.Username, ldapLogin.Password).ConfigureAwait(false);
                await LogInToRestApi();
                return res;
            }
            if (loginOption is EmailLoginOption emailLogin)
            {
                Password = emailLogin.Password;
                var res = await LoginWithEmailAsync(emailLogin.Email, emailLogin.Password).ConfigureAwait(false);
                await LogInToRestApi();
                return res;
            }
            if (loginOption is UsernameLoginOption usernameLogin)
            {
                Username = usernameLogin.Username;
                Password = usernameLogin.Password;
                var res = await LoginWithUsernameAsync(usernameLogin.Username, usernameLogin.Password).ConfigureAwait(false);
                await LogInToRestApi();
                return res;
            }
            if (loginOption is ResumeLoginOption resumeLogin)
            {
                return await LoginResumeAsync(resumeLogin.Token).ConfigureAwait(false);
            }

            throw new NotSupportedException($"The given login option `{loginOption.GetType()}` is not supported.");
        }

        public async Task<MethodResult<LoginResult>> LoginWithEmailAsync(string email, string password)
        {
            _logger.LogInformation($"Logging in with user {email} using an email...");
            var passwordHash = EncodingHelper.Sha256Hash(password);
            var request = new
            {
                user = new
                {
                    email
                },
                password = new
                {
                    digest = passwordHash,
                    algorithm = EncodingHelper.Sha256
                }
            };

            return await InternalLoginAsync(request).ConfigureAwait(false);
        }

        public async Task<MethodResult<LoginResult>> LoginWithUsernameAsync(string username, string password)
        {
            _logger.LogInformation($"Logging in with user {username} using a username...");
            var passwordHash = EncodingHelper.Sha256Hash(password);
            var request = new
            {
                user = new
                {
                    username
                },
                password = new
                {
                    digest = passwordHash,
                    algorithm = EncodingHelper.Sha256
                }
            };

            return await InternalLoginAsync(request).ConfigureAwait(false);
        }

        public async Task<MethodResult<LoginResult>> LoginWithLdapAsync(string username, string password)
        {
            _logger.LogInformation($"Logging in with user {username} using LDAP...");
            var request = new
            {
                username,
                ldapPass = password,
                ldap = true,
                ldapOptions = new {}
            };

            return await InternalLoginAsync(request).ConfigureAwait(false);
        }

        public async Task<MethodResult<LoginResult>> LoginResumeAsync(string sessionToken)
        {
            _logger.LogInformation($"Resuming session {sessionToken}");
            var request = new
            {
                resume = sessionToken
            };

            return await InternalLoginAsync(request).ConfigureAwait(false);
        }

        public async Task<MethodResult<LoginResult>> GetNewTokenAsync()
        {
            var result = await _client.CallAsync("getNewToken", TimeoutToken).ConfigureAwait(false);
            var loginResult = result.ToObject<MethodResult<LoginResult>>(JsonSerializer);
            if (!loginResult.HasError)
            {
                await SetDriverUserInfoAsync(loginResult.Result.UserId).ConfigureAwait(false);
            }

            return loginResult;
        }

        public async Task<MethodResult> RemoveOtherTokensAsync()
        {
            var result = await _client.CallAsync("removeOtherTokens", TimeoutToken).ConfigureAwait(false);
            return result.ToObject<MethodResult>(JsonSerializer);
        }

        private async Task<MethodResult<LoginResult>> InternalLoginAsync(object request)
        {
            var data = await _client.CallAsync("login", TimeoutToken, request).ConfigureAwait(false);
            var result = data.ToObject<MethodResult<LoginResult>>(JsonSerializer);
            if (!result.HasError)
            {
                await SetDriverUserInfoAsync(result.Result.UserId).ConfigureAwait(false);
            }
            return result;
        }

        private async Task SetDriverUserInfoAsync(string userId)
        {
            UserId = userId;
            var collection = await _collectionDatabase.WaitForObjectInCollectionAsync("users", userId, TimeoutToken).ConfigureAwait(false);
            var user = collection.GetById<FullUser>(userId);
            Username = user?.Username;
        }

        public async Task<JObject> RegisterUserAsync(string name, string emailOrUsername, string password)
        {
            var obj = new Dictionary<string, string>
            {
                {"name", name},
                {"emailOrUsername", emailOrUsername},
                {"pass", password},
                {"confirm-pass", password}
            };

            var result = await _client.CallAsync("registerUser", TimeoutToken, obj).ConfigureAwait(false);
            return result?["result"].ToObject<JObject>(JsonSerializer);
        }

        public async Task<MethodResult> SetReactionAsync(string reaction, string messageId)
        {
            var result = await _client.CallAsync("setReaction", TimeoutToken, reaction, messageId).ConfigureAwait(false);
            return result.ToObject<MethodResult>(JsonSerializer);
        }

        public async Task<MethodResult<string>> GetRoomIdAsync(string roomIdOrName)
        {
            _logger.LogInformation($"Looking up Room ID for: #{roomIdOrName}");
            var result = await _client.CallAsync("getRoomIdByNameOrId", TimeoutToken, roomIdOrName).ConfigureAwait(false);

            return result.ToObject<MethodResult<string>>(JsonSerializer);
        }

        public async Task<MethodResult> DeleteMessageAsync(string messageId, string roomId)
        {
            _logger.LogInformation($"Deleting message {messageId}");
            var request = new
            {
                rid = roomId,
                _id = messageId
            };
            var result = await _client.CallAsync("deleteMessage", TimeoutToken, request).ConfigureAwait(false);
            return result.ToObject<MethodResult>(JsonSerializer);
        }

        public async Task<MethodResult<CreateRoomResult>> CreatePrivateMessageAsync(string username)
        {
            _logger.LogInformation($"Creating private message with {username}");
            var result = await _client.CallAsync("createDirectMessage", TimeoutToken, username).ConfigureAwait(false);
            return result.ToObject<MethodResult<CreateRoomResult>>(JsonSerializer);
        }

        public async Task<MethodResult<ChannelListResult>> ChannelListAsync()
        {
            _logger.LogInformation("Looking up public channels.");
            var result = await _client.CallAsync("channelsList", TimeoutToken).ConfigureAwait(false);
            return result.ToObject<MethodResult<ChannelListResult>>(JsonSerializer);
        }

        public async Task<MethodResult> JoinRoomAsync(string roomId)
        {
            _logger.LogInformation($"Joining Room: #{roomId}");
            var result = await _client.CallAsync("joinRoom", TimeoutToken, roomId).ConfigureAwait(false);
            return result.ToObject<MethodResult>(JsonSerializer);
        }

        public async Task<MethodResult<RocketMessage>> SendMessageAsync(string text, string roomId)
        {
            _logger.LogInformation($"Sending message to #{roomId}: {text}");
            var request = new
            {
                msg = text,
                rid = roomId,
                bot = IsBot
            };
            var result = await _client.CallAsync("sendMessage", TimeoutToken, request).ConfigureAwait(false);
            return result.ToObject<MethodResult<RocketMessage>>(JsonSerializer);
        }

        public async Task<MethodResult<RocketMessage>> SendCustomMessageAsync(Attachment attachment, string roomId)
        {
            var request = new
            {
                msg = "",
                rid = roomId,
                bot = IsBot,
                attachments = new[]
                {
                    attachment
                }
            };
            var result = await _client.CallAsync("sendMessage", TimeoutToken, request).ConfigureAwait(false);
            return result.ToObject<MethodResult<RocketMessage>>(JsonSerializer);
        }

        public async Task<MethodResult> UpdateMessageAsync(string messageId, string roomId, string newMessage)
        {
            _logger.LogInformation($"Updating message {messageId}");
            var request = new
            {
                msg = newMessage,
                rid = roomId,
                bot = IsBot,
                _id = messageId
            };
            var result = await _client.CallAsync("updateMessage", TimeoutToken, request).ConfigureAwait(false);
            return result.ToObject<MethodResult>(JsonSerializer);
        }

        public async Task<MethodResult<LoadMessagesResult>> LoadMessagesAsync(string roomId, DateTime? end = null,
                                                                              int? limit = 20,
                                                                              string ls = null)
        {
            _logger.LogInformation($"Loading messages from #{roomId}");

            var rawMessage = await _client.CallAsync("loadHistory", TimeoutToken, roomId, end, limit, ls).ConfigureAwait(false);
            var messageResult = rawMessage.ToObject<MethodResult<LoadMessagesResult>>(JsonSerializer);
            return messageResult;
        }

        public async Task<MethodResult<LoadMessagesResult>> SearchMessagesAsync(string query, string roomId,
                                                                                int limit = 100)
        {
            _logger.LogInformation($"Searching for messages in #{roomId} using `{query}`.");

            var rawMessage = await _client.CallAsync("messageSearch", TimeoutToken, query, roomId, limit).ConfigureAwait(false);
            var messageResult = rawMessage.ToObject<MethodResult<LoadMessagesResult>>(JsonSerializer);
            return messageResult;
        }

        public async Task<MethodResult<StatisticsResult>> GetStatisticsAsync(bool refresh = false)
        {
            _logger.LogInformation("Requesting statistics.");
            var results = await _client.CallAsync("getStatistics", TimeoutToken).ConfigureAwait(false);

            return results.ToObject<MethodResult<StatisticsResult>>(JsonSerializer);
        }

        public async Task<MethodResult<CreateRoomResult>> CreateChannelAsync(string roomName, IList<string> members = null)
        {
            _logger.LogInformation($"Creating room {roomName}.");
            var results =
                await _client.CallAsync("createChannel", TimeoutToken, roomName, members ?? new List<string>()).ConfigureAwait(false);

            return results.ToObject<MethodResult<CreateRoomResult>>(JsonSerializer);
        }

        public async Task<MethodResult<CreateRoomResult>> HideRoomAsync(string roomId)
        {
            _logger.LogInformation($"Hiding room {roomId}.");
            var results =
                await _client.CallAsync("hideRoom", TimeoutToken, roomId).ConfigureAwait(false);

            return results.ToObject<MethodResult<CreateRoomResult>>(JsonSerializer);
        }

        public async Task<MethodResult<int>> EraseRoomAsync(string roomId)
        {
            _logger.LogInformation($"Deleting room {roomId}.");
            var results =
                await _client.CallAsync("eraseRoom", TimeoutToken, roomId).ConfigureAwait(false);

            return results.ToObject<MethodResult<int>>(JsonSerializer);
        }

        public async Task<MethodResult> ResetAvatarAsync()
        {
            var results = await _client.CallAsync("resetAvatar", TimeoutToken).ConfigureAwait(false);
            return results.ToObject<MethodResult>(JsonSerializer);
        }

        public async Task<MethodResult> SetAvatarFromUrlAsync(string url)
        {
            var results = await _client.CallAsync("setAvatarFromService", TimeoutToken, url, "", "url").ConfigureAwait(false);
            return results.ToObject<MethodResult>(JsonSerializer);
        }

        public async Task<MethodResult> SetAvatarFromImageStreamAsync(Stream sourceStream, string mimeType)
        {
            var base64 = EncodingHelper.ConvertToBase64(sourceStream);
            var end = $"data:{mimeType};base64,{base64}";
            var results = await _client.CallAsync("setAvatarFromService", TimeoutToken, end, mimeType, "upload").ConfigureAwait(false);
            return results.ToObject<MethodResult>(JsonSerializer);
        }

        public async Task<MethodResult<RocketMessage>> PinMessageAsync(RocketMessage message)
        {
            var results =
                await _client.CallAsync("pinMessage", TimeoutToken, message).ConfigureAwait(false);

            return results.ToObject<MethodResult<RocketMessage>>(JsonSerializer);
        }

        public async Task<MethodResult> UnpinMessageAsync(RocketMessage message)
        {
            var results =
                await _client.CallAsync("unpinMessage", TimeoutToken, message).ConfigureAwait(false);

            return results.ToObject<MethodResult>(JsonSerializer);
        }

        public async Task<MethodResult<int>> UploadFileAsync(string roomId)
        {
            var results =
                await _client.CallAsync("/rocketchat_uploads/insert", TimeoutToken, roomId).ConfigureAwait(false);

            return results.ToObject<MethodResult<int>>(JsonSerializer);
        }

        private void OnMessageReceived(RocketMessage rocketmessage)
        {
            MessageReceived?.Invoke(rocketmessage);
        }

        public IStreamCollection GetCollection(string collectionName)
        {
            IStreamCollection value;
            var results = _collectionDatabase.TryGetCollection(collectionName, out value);

            return results ? value : null;
        }

        [Obsolete("Use the property Rooms instead. This will be removed in version 2.")]
        public IEnumerable<Room> GetRooms()
        {
            var collection = GetRoomsCollection();
            if (collection == null)
            {
                yield break;
            }

            var rooms = collection.Items();
            foreach (var room in rooms.Select(x => x.Value))
            {
                yield return room;
            }
        }

        [CanBeNull]
        public TypedStreamCollection<Room> GetRoomsCollection()
        {
            IStreamCollection value;
            var results = _collectionDatabase.TryGetCollection("rocketchat_subscription", out value);
            if (!results)
            {
                return null;
            }

            var typedCollection = new TypedStreamCollection<Room>(value);
            return typedCollection;
        }

        [CanBeNull]
        public TypedStreamCollection<RoomInfo> GetRoomInfoCollection()
        {
            IStreamCollection value;
            var results = _collectionDatabase.TryGetCollection("rocketchat_room", out value);
            if (!results)
            {
                return null;
            }

            var typedCollection = new TypedStreamCollection<RoomInfo>(value);
            return typedCollection;
        }

        public void Dispose()
        {
            _client.Dispose();
        }

        private void OnDdpReconnect()
        {
            DdpReconnect?.Invoke();
        }

        private CancellationToken CreateTimeoutToken()
        {
            const int timeoutSeconds = 60;
            var source = new CancellationTokenSource();
            source.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            
            return source.Token;
        }
    }
}

namespace System.Net.Http
{
    public class JsonContent : StringContent
    {
        public JsonContent(object obj) :
            base(JsonConvert.SerializeObject(obj), Encoding.UTF8, "application/json")
        { }
    }
}