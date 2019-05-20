using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Rocket.Chat.Net.Bot
{
    public static class RocketChatBotExtensions
    {
        private static readonly Dictionary<Stream, Func<RocketChatBot, object[], (string streamname, string eventname, object[] parameters)>> streamconverters = new Dictionary<Stream, Func<RocketChatBot, object[], (string streamname, string eventname, object[] parameters)>>()
        {
            { Stream.NotifyUser_Notifications, (RocketChatBot bot, object[] parameters) => ("stream-notify-user", $"{bot.UserId}/notification", parameters?.Length > 0 ? parameters : new object[] { false })},
            { Stream.NotifyUser_Message, (RocketChatBot bot, object[] parameters) =>  ("stream-notify-user", $"{bot.UserId}/message", parameters?.Length > 0 ? parameters : new object[] { false }) },
            { Stream.NotifyUser_RoomsChanged, (RocketChatBot bot, object[] parameters) =>  ("stream-notify-user", $"{bot.UserId}/rooms-changed", parameters?.Length > 0 ? parameters : new object[] { false }) },
            { Stream.NotifyUser_SubscriptionsChanged, (RocketChatBot bot, object[] parameters) =>  ("stream-notify-user", $"{bot.UserId}/subscriptions-changed", parameters?.Length > 0 ? parameters : new object[] { false }) },
            { Stream.NotifyUser_Webrtc, (RocketChatBot bot, object[] parameters) =>  ("stream-notify-user", $"{bot.UserId}/webrtc", parameters?.Length > 0 ? parameters : new object[] { false }) },
            { Stream.NotifyUser_Otr, (RocketChatBot bot, object[] parameters) =>  ("stream-notify-user", $"{bot.UserId}/otr", parameters?.Length > 0 ? parameters : new object[] { false }) },
        };

        public static async Task<string> SubscribeAsync(this RocketChatBot bot, Stream stream, params object[] parameters)
        {
            (string streamname, string eventname, object[] parameters) p = streamconverters[stream](bot, parameters);
            await bot.SubscribeAsync(p.streamname, p.eventname, false);
            return p.streamname + "/" + p.eventname;
        }

        public static Stream GetStream(this RocketChatBot bot, string name)
        {
            return Stream.NotifyUser_Notifications;
        }
    }

    public enum Stream
    {
        NotifyUser_Notifications,
        NotifyUser_Message,
        NotifyUser_RoomsChanged,
        NotifyUser_SubscriptionsChanged,
        NotifyUser_Webrtc,
        NotifyUser_Otr
    }
}
