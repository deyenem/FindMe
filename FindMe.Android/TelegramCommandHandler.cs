using Android.App;
using Android.Content;
using Android.Util;
using Newtonsoft.Json;
using System;
using Xamarin.Essentials;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FindMe.Models;

namespace FindMe.Droid
{
    [Service]
    public class TelegramCommandHandler
    {
        private const string TAG = "TelegramCommandHandler";
        private readonly string settingsFilePath;
        private HttpClient httpClient;
        private Timer commandCheckTimer;
        private long lastUpdateId = 0;
        private Context context;

        public TelegramCommandHandler(Context context)
        {
            this.context = context;
            settingsFilePath = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.Personal), "secure_settings.json");
            httpClient = new HttpClient();
        }

        public void Start()
        {
            // Check if credentials are available before starting timer
            var settings = LoadSettings();
            bool credentialsAvailable = !string.IsNullOrEmpty(settings.BotToken) && !string.IsNullOrEmpty(settings.ChatId);

            if (credentialsAvailable)
            {
                // Check for commands every 10 seconds
                commandCheckTimer = new Timer(CheckForCommands, null, 0, 10000);
                Log.Info(TAG, "Telegram command handler started");
            }
            else
            {
                Log.Warn(TAG, "Telegram command handler not started - missing credentials");
            }
        }

        public void Stop()
        {
            commandCheckTimer?.Dispose();
            commandCheckTimer = null;
        }

        private async void CheckForCommands(object state)
        {
            try
            {
                var settings = LoadSettings();

                // Check if credentials are available
                if (string.IsNullOrEmpty(settings.BotToken) || string.IsNullOrEmpty(settings.ChatId))
                {
                    // Skip command checking when credentials aren't available
                    return;
                }

                string url = $"https://api.telegram.org/bot{settings.BotToken}/getUpdates?offset={lastUpdateId + 1}";
                var response = await httpClient.GetStringAsync(url);
                var updates = JsonConvert.DeserializeObject<TelegramUpdateResponse>(response);

                if (updates?.Result == null || updates.Result.Length == 0)
                {
                    return;
                }

                foreach (var update in updates.Result)
                {
                    // Update last processed ID
                    if (update.UpdateId > lastUpdateId)
                    {
                        lastUpdateId = update.UpdateId;
                    }

                    // Only process messages from authorized chat ID
                    if (update.Message?.Chat?.Id.ToString() != settings.ChatId)
                    {
                        continue;
                    }

                    // Process commands
                    string text = update.Message?.Text;
                    if (!string.IsNullOrEmpty(text) && text.StartsWith("/"))
                    {
                        await ProcessCommand(text, settings);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(TAG, $"Error checking for commands: {ex.Message}");
            }
        }

        private async Task ProcessCommand(string command, AppSettings currentSettings)
        {
            try
            {
                string response = "Unknown command. Available commands:\n" +
                    "/interval [milliseconds] - Set tracking interval\n" +
                    "/status - Get current settings\n" +
                    "/start - Start tracking\n" +
                    "/stop - Stop tracking\n" +
                    "/token [newtoken] - Change bot token\n" +
                    "/chatid [newchatid] - Change chat ID";

                // Split command and parameters
                string[] parts = command.Split(new[] { ' ' }, 2);
                string cmd = parts[0].ToLower();
                string param = parts.Length > 1 ? parts[1] : null;

                switch (cmd)
                {
                    case "/interval":
                        if (!string.IsNullOrEmpty(param) && int.TryParse(param, out int interval))
                        {
                            var Interval = interval.ToString();
                            // Update interval
                            currentSettings.Interval = Interval;

                            SaveSettings(currentSettings);
                            StopLocationService();
                            await StopTracking();
                            await SecureStorage.SetAsync("Interval", Interval);
                            StartLocationService();
                            response = $"Interval updated to {interval} milliseconds";
                        }
                        else
                        {
                            response = "Please specify a valid interval in milliseconds";
                        }
                        break;

                    case "/status":
                        bool isActive = IsServiceRunning();
                        response = $"Status: {(isActive ? "Tracking active" : "Tracking inactive")}\n" +
                            $"Bot token: {currentSettings.BotToken}\n" +
                            $"Chat ID: {currentSettings.ChatId}\n" +
                            $"Interval: {currentSettings.Interval} ms";
                        break;

                    case "/start":
                        StartLocationService();
                        response = "Location tracking started";
                        break;

                    case "/stop":
                        StopLocationService();
                        await StopTracking();
                        response = "Location tracking stopped";
                        break;

                    case "/token":
                        if (!string.IsNullOrEmpty(param))
                        {
                            var botToken = param;

                            currentSettings.BotToken = botToken;
                            SaveSettings(currentSettings);
                            await SecureStorage.SetAsync("bot_token", botToken);
                            StartLocationService();
                            response = "Bot token updated";
                        }
                        else
                        {
                            response = "Please specify a valid bot token";
                        }
                        break;

                    case "/chatid":
                        if (!string.IsNullOrEmpty(param))
                        {
                            var chatId = param;

                            currentSettings.ChatId = chatId;
                            SaveSettings(currentSettings);
                            await SecureStorage.SetAsync("chat_id", chatId);
                            StartLocationService();
                            response = "Chat ID updated";
                        }
                        else
                        {
                            response = "Please specify a valid chat ID";
                        }
                        break;
                }

                // Send response back to Telegram
                await SendTelegramMessage(currentSettings.BotToken, currentSettings.ChatId, response);
            }
            catch (Exception ex)
            {
                Log.Error(TAG, $"Error processing command: {ex.Message}");
            }
        }

        private async Task SendTelegramMessage(string botToken, string chatId, string message)
        {
            try
            {
                // Don't attempt to send if credentials are missing
                if (string.IsNullOrEmpty(botToken) || string.IsNullOrEmpty(chatId))
                {
                    Log.Warn(TAG, "Attempted to send Telegram message with missing credentials");
                    return;
                }

                string url = $"https://api.telegram.org/bot{botToken}/sendMessage?chat_id={chatId}&text={Uri.EscapeDataString(message)}";
                await httpClient.GetStringAsync(url);
            }
            catch (Exception ex)
            {
                Log.Error(TAG, $"Error sending Telegram message: {ex.Message}");
            }
        }

        private AppSettings LoadSettings()
        {
            try
            {
                if (File.Exists(settingsFilePath))
                {
                    var json = File.ReadAllText(settingsFilePath);
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json);

                    // Return the settings without hardcoded fallbacks
                    return settings ?? new AppSettings();
                }
                else
                {
                    // Return empty settings instead of hardcoded values
                    return new AppSettings();
                }
            }
            catch
            {
                // Return empty settings instead of hardcoded values
                return new AppSettings();
            }
        }

        private void SaveSettings(AppSettings settings)
        {
            try
            {
                string json = JsonConvert.SerializeObject(settings);
                File.WriteAllText(settingsFilePath, json);
            }
            catch (Exception ex)
            {
                Log.Error(TAG, $"Error saving settings: {ex.Message}");
            }
        }

        private bool IsServiceRunning()
        {
            var preferences = Android.Preferences.PreferenceManager.GetDefaultSharedPreferences(context);
            return preferences.GetBoolean("is_tracking_service_running", false);
        }

        private void StartLocationService()
        {
            try
            {
                var intent = new Intent(context, typeof(BackgroundLocationService));
                if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.O)
                {
                    context.StartForegroundService(intent);
                }
                else
                {
                    context.StartService(intent);
                }

                // Update preferences
                var preferences = Android.Preferences.PreferenceManager.GetDefaultSharedPreferences(context);
                var editor = preferences.Edit();
                editor.PutBoolean("is_tracking_service_running", true);
                editor.Apply();
            }
            catch (Exception ex)
            {
                Log.Error(TAG, $"Error starting service: {ex.Message}");
            }
        }

        private void StopLocationService()
        {
            try
            {
                var intent = new Intent(context, typeof(BackgroundLocationService));
                context.StopService(intent);

                // Update preferences
                var preferences = Android.Preferences.PreferenceManager.GetDefaultSharedPreferences(context);
                var editor = preferences.Edit();
                editor.PutBoolean("is_tracking_service_running", false);
                editor.Apply();
            }
            catch (Exception ex)
            {
                Log.Error(TAG, $"Error stopping service: {ex.Message}");
            }
        }

        public Task StopTracking()
        {
            try
            {
                var intent = new Intent(context, typeof(BackgroundLocationService));

                BackgroundLocationService.IsStoppingByUserRequest = true;

                // Update preferences
                var preferences = Android.Preferences.PreferenceManager.GetDefaultSharedPreferences(context);
                var editor = preferences.Edit();
                editor.PutBoolean("is_tracking_service_running", false);
                editor.Apply();

                context.StopService(intent);
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                return Task.FromException(ex);
            }
            finally
            {
                Task.Delay(2000).ContinueWith(_ => {
                    BackgroundLocationService.IsStoppingByUserRequest = false;
                });
            }
        }
    }
}