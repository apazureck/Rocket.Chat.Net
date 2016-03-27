﻿namespace Rocket.Chat.Net.Tests.Driver
{
    using System.Threading;
    using System.Threading.Tasks;

    using Newtonsoft.Json.Linq;

    using NSubstitute;

    using Ploeh.AutoFixture;

    using Rocket.Chat.Net.Driver;
    using Rocket.Chat.Net.Helpers;
    using Rocket.Chat.Net.Interfaces;
    using Rocket.Chat.Net.Models.Results;
    using Rocket.Chat.Net.Tests.Helpers;

    using Xunit;

    public class RocketChatDriverFacts
    {
        private readonly Fixture _autoFixture = new Fixture();
        private readonly IDdpClient _mockClient;
        private readonly IStreamCollectionDatabase _collectionDatabase;
        private readonly IRocketChatDriver _driver;

        private static CancellationToken CancellationToken => Arg.Any<CancellationToken>();

        public RocketChatDriverFacts()
        {
            _mockClient = Substitute.For<DummyDdpClient>();
            _collectionDatabase = Substitute.For<IStreamCollectionDatabase>();
            var mockLog = Substitute.For<ILogger>();
            _driver = new RocketChatDriver(mockLog, _mockClient, _collectionDatabase);
        }

        [Fact]
        public async Task Connect_should_connect_the_client()
        {
            // Act
            await _driver.ConnectAsync();

            // Assert
            await _mockClient.Received().ConnectAsync(CancellationToken);
        }

        [Fact]
        public async Task When_subscribing_to_channel_sub_with_client()
        {
            // Act
            await _driver.SubscribeToRoomAsync();

            // Assert
            await _mockClient.Received().SubscribeAsync("stream-messages", CancellationToken, null, "10");
        }

        [Fact]
        public async Task When_subscribing_to_ome_channel_sub_with_client()
        {
            var room = _autoFixture.Create<string>();

            // Act
            await _driver.SubscribeToRoomAsync(room);

            // Assert
            await _mockClient.Received().SubscribeAsync("stream-messages", CancellationToken, room, "10");
        }

        [Fact]
        public async Task Ping_server_uses_client_ping()
        {
            // Act
            await _driver.PingAsync();

            // Assert
            await _mockClient.Received().PingAsync(CancellationToken);
        }

        [Fact]
        public async Task Login_with_email()
        {
            var email = _autoFixture.Create<string>();
            var password = _autoFixture.Create<string>();
            var payload = new
            {
                user = new
                {
                    email
                },
                password = new
                {
                    digest = DriverHelper.Sha256Hash(password),
                    algorithm = DriverHelper.Sha256
                }
            };

            var loginResult = _autoFixture.Create<LoginResult>();
            var loginResponse = JObject.FromObject(new
            {
                result = loginResult
            });

            _mockClient.CallAsync(Arg.Any<string>(), CancellationToken, Arg.Any<object[]>())
                       .ReturnsForAnyArgs(Task.FromResult(loginResponse));

            IStreamCollection collection = new StreamCollection("users");
            var user = JObject.FromObject(new {username = ""});
            collection.Added(loginResult.UserId, user);
            _collectionDatabase.WaitForCollectionAsync("users", loginResult.UserId, CancellationToken)
                               .Returns(Task.FromResult(collection));

            // Act
            await _driver.LoginWithEmailAsync(email, password);

            // Assert
            await _mockClient.ReceivedWithAnyArgs().CallAsync("login", CancellationToken, payload);
        }

        [Fact]
        public void Disposing_driver_should_dispose_client()
        {
            // Act
            _driver.Dispose();

            // Assert
            _mockClient.Received().Dispose();
        }

        [Fact]
        public void Added_message_should_add_to_a_streaming_collection()
        {
            var callingClient = (DummyDdpClient) _mockClient;
            var payload = new
            {
                collection = _autoFixture.Create<string>(),
                id = _autoFixture.Create<string>(),
                fields = new
                {
                    id = _autoFixture.Create<string>()
                }
            };

            var mockCollection = Substitute.For<IStreamCollection>();
            _collectionDatabase.GetOrAddCollection(payload.collection).Returns(mockCollection);

            // Act
            callingClient.CallDataReceivedRaw("added", JObject.FromObject(payload));

            // Assert
            mockCollection.Received().Added(payload.id, Arg.Any<JObject>());
        }

        [Fact]
        public void Changed_message_should_change_a_streaming_collection()
        {
            var callingClient = (DummyDdpClient) _mockClient;
            var payload = new
            {
                collection = _autoFixture.Create<string>(),
                id = _autoFixture.Create<string>(),
                fields = new
                {
                    id = _autoFixture.Create<string>()
                }
            };

            var mockCollection = Substitute.For<IStreamCollection>();
            _collectionDatabase.GetOrAddCollection(payload.collection).Returns(mockCollection);

            // Act
            callingClient.CallDataReceivedRaw("changed", JObject.FromObject(payload));

            // Assert
            mockCollection.Received().Changed(payload.id, Arg.Any<JObject>());
        }

        [Fact]
        public void Removed_message_should_change_a_streaming_collection()
        {
            var callingClient = (DummyDdpClient) _mockClient;
            var payload = new
            {
                collection = _autoFixture.Create<string>(),
                id = _autoFixture.Create<string>()
            };

            var mockCollection = Substitute.For<IStreamCollection>();
            _collectionDatabase.GetOrAddCollection(payload.collection).Returns(mockCollection);

            // Act
            callingClient.CallDataReceivedRaw("removed", JObject.FromObject(payload));

            // Assert
            mockCollection.Received().Removed(payload.id);
        }

        [Fact]
        public void Non_streaming_messages_should_not_change_collections()
        {
            var callingClient = (DummyDdpClient) _mockClient;
            var payload = new
            {
                id = _autoFixture.Create<string>(),
                msg = _autoFixture.Create<string>(),
                random = _autoFixture.Create<int>()
            };

            // Act
            callingClient.CallDataReceivedRaw(payload.msg, JObject.FromObject(payload));

            // Assert
            _collectionDatabase.DidNotReceive().GetOrAddCollection(Arg.Any<string>());
        }
    }
}