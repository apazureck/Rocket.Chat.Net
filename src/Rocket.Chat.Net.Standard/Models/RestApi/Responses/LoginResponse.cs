using Newtonsoft.Json;

namespace Rocket.Chat.Net.Standard.Models.RestApi.Responses
{
    public class RocketChatRestResponse<Tdata>
    {
        public string Status { get; set; }
        public Tdata Data { get; set; }
    }

    public class LoginResponseData
    {
        public string AuthToken { get; set; }
        public string UserId { get; set; }
        public UserData Me { get; set; }
    }

    public class UserData
    {
        [JsonProperty("_id")]
        public string Id { get; set; }
        public string Name { get; set; }

        //todo: add rest of properties from https://rocket.chat/docs/developer-guides/rest-api/authentication/login/
    }
}
