namespace Rocket.Chat.Net.Bot.Models
{
    using Rocket.Chat.Net.Bot.Interfaces;

    /// <summary>
    /// A basic rocket response message.
    /// </summary>
    public class BasicResponse : IMessageResponse
    {
        /// <summary>
        /// Text of message.
        /// </summary>
        public string Message { get; set; }

        /// <summary>
        /// Id of room to send this message to.
        /// </summary>
        public string RoomId { get; set; }

        /// <summary>
        /// Creates a new basic response
        /// </summary>
        /// <param name="message">message to send</param>
        /// <param name="roomId">room id or leave null if the response should go to the same room</param>
        public BasicResponse(string message, string roomId = null)
        {
            Message = message;
            RoomId = roomId;
        }
    }
}