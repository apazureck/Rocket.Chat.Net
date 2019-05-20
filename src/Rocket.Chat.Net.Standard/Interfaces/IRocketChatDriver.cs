namespace Rocket.Chat.Net.Interfaces
{
    using System;
    using Newtonsoft.Json.Linq;
    using Rocket.Chat.Net.Interfaces.Driver;

    public interface IRocketChatDriver : IDisposable,
                                         IRocketClientManagement,
                                         IRocketUserManagement,
                                         IRocketMessagingManagement,
                                         IRocketRoomManagement,
                                         IRocketAdministrativeManagement
    {
        event Action<string, JObject> CollectionChanged;
    }
}