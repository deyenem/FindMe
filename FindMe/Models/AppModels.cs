using Newtonsoft.Json;

namespace FindMe.Models
{
    /// <summary>
    /// Settings model for the application configuration
    /// </summary>
    public class AppSettings
    {
        public string BotToken { get; set; } = string.Empty;
        public string ChatId { get; set; } = string.Empty;
        public string Interval { get; set; } = "60000"; // Reasonable default interval
    }

    /// <summary>
    /// Models for Telegram API responses
    /// </summary>
    public class TelegramUpdateResponse
    {
        public bool Ok { get; set; }
        public TelegramUpdate[] Result { get; set; }
    }

    public class TelegramUpdate
    {
        [JsonProperty("update_id")]
        public long UpdateId { get; set; }

        public TelegramMessage Message { get; set; }
    }

    public class TelegramMessage
    {
        [JsonProperty("message_id")]
        public long MessageId { get; set; }

        public TelegramChat Chat { get; set; }

        public string Text { get; set; }
    }

    public class TelegramChat
    {
        public long Id { get; set; }
    }
}