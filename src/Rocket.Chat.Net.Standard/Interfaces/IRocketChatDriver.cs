namespace Rocket.Chat.Net.Interfaces
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Newtonsoft.Json.Linq;
    using Rocket.Chat.Net.Interfaces.Driver;
    using Rocket.Chat.Net.Models;

    public interface IRocketChatDriver : IDisposable,
                                         IRocketClientManagement,
                                         IRocketUserManagement,
                                         IRocketMessagingManagement,
                                         IRocketRoomManagement,
                                         IRocketAdministrativeManagement
    {
        event Action<string, JObject> CollectionChanged;
        Task<List<FullUser>> GetUserList();
    }
}