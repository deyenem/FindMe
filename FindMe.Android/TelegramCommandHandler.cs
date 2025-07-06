using Android.App;
using Android.Content;
using Newtonsoft.Json;
using System;
using Xamarin.Essentials;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
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
        private GeoJsonManager geoJsonManager;

        // Static variables to prevent infinite loops and message spam
        private static DateTime lastIntervalUpdate = DateTime.MinValue;
        private static DateTime lastStartupMessage = DateTime.MinValue;
        private const int INTERVAL_UPDATE_COOLDOWN_SECONDS = 30;
        private const int STARTUP_MESSAGE_COOLDOWN_SECONDS = 60;

        public TelegramCommandHandler(Context context)
        {
            this.context = context;
            settingsFilePath = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.Personal), "secure_settings.json");
            httpClient = new HttpClient();
            geoJsonManager = new GeoJsonManager(context);
        }

        public void Start()
        {
            try
            {
                var settings = LoadSettings();
                bool credentialsAvailable = !string.IsNullOrEmpty(settings.BotToken) && !string.IsNullOrEmpty(settings.ChatId);

                if (credentialsAvailable)
                {
                    commandCheckTimer = new Timer(CheckForCommands, null, 0, 10000);

                    // Only send startup message if enough time has passed since last one
                    if ((DateTime.Now - lastStartupMessage).TotalSeconds > STARTUP_MESSAGE_COOLDOWN_SECONDS)
                    {
                        lastStartupMessage = DateTime.Now;
                        _ = Task.Run(async () => {
                            await Task.Delay(2000);
                            await SendTelegramMessage(settings.BotToken, settings.ChatId, "🤖 FindMe service started");
                        });
                    }
                }
            }
            catch
            {
                // Silent fail
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

                if (string.IsNullOrEmpty(settings.BotToken) || string.IsNullOrEmpty(settings.ChatId))
                {
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
                    if (update.UpdateId > lastUpdateId)
                    {
                        lastUpdateId = update.UpdateId;
                    }

                    if (update.Message?.Chat?.Id.ToString() != settings.ChatId)
                    {
                        continue;
                    }

                    string text = update.Message?.Text;
                    if (!string.IsNullOrEmpty(text) && text.StartsWith("/"))
                    {
                        await ProcessCommand(text, settings);
                    }
                }
            }
            catch
            {
                // Silent fail
            }
        }

        private async Task ProcessCommand(string command, AppSettings currentSettings)
        {
            try
            {
                string response = "Unknown command. Available commands:\n" +
                    "/interval [milliseconds] - Set tracking interval\n" +
                    "/status - Get current settings and stats\n" +
                    "/start - Start tracking\n" +
                    "/stop - Stop tracking\n" +
                    "/restart - Restart tracking service\n" +
                    "/token [newtoken] - Change bot token\n" +
                    "/chatid [newchatid] - Change chat ID\n" +
                    "/report [date] - Get GeoJSON report for specific date (YYYY-MM-DD)\n" +
                    "/today - Get today's tracking report\n" +
                    "/yesterday - Get yesterday's tracking report\n" +
                    "/files - List available data files\n" +
                    "/cleanup [days] - Delete files older than X days";

                string[] parts = command.Split(new[] { ' ' }, 2);
                string cmd = parts[0].ToLower();
                string param = parts.Length > 1 ? parts[1] : null;

                switch (cmd)
                {
                    case "/interval":
                        // FIXED: Prevent rapid interval changes that cause infinite loops
                        if ((DateTime.Now - lastIntervalUpdate).TotalSeconds < INTERVAL_UPDATE_COOLDOWN_SECONDS)
                        {
                            response = $"⏱️ Please wait {INTERVAL_UPDATE_COOLDOWN_SECONDS} seconds between interval changes";
                            break;
                        }

                        if (!string.IsNullOrEmpty(param) && int.TryParse(param, out int interval))
                        {
                            lastIntervalUpdate = DateTime.Now;

                            var Interval = interval.ToString();
                            currentSettings.Interval = Interval;

                            // Save settings without restarting services
                            SaveSettings(currentSettings);
                            await SecureStorage.SetAsync("Interval", Interval);

                            // Send broadcast to update existing service interval
                            var intent = new Intent("com.findme.UPDATE_INTERVAL");
                            intent.PutExtra("new_interval", interval);
                            context.SendBroadcast(intent);

                            response = $"⏱️ Interval updated to {interval} milliseconds. Changes will take effect shortly.";
                        }
                        else
                        {
                            response = "❌ Please specify a valid interval in milliseconds";
                        }
                        break;

                    case "/status":
                        bool isActive = IsServiceRunning();
                        var files = geoJsonManager.GetAvailableDataFiles();
                        response = $"🔹 Status: {(isActive ? "✅ Tracking active" : "❌ Tracking inactive")}\n" +
                            $"🔹 Bot token: {MaskToken(currentSettings.BotToken)}\n" +
                            $"🔹 Chat ID: {currentSettings.ChatId}\n" +
                            $"🔹 Interval: {currentSettings.Interval} ms\n" +
                            $"🔹 Available data files: {files.Count}\n" +
                            $"🔹 Device time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
                        break;

                    case "/start":
                        StartLocationService();
                        response = "✅ Location tracking started";
                        break;

                    case "/stop":
                        StopLocationService();
                        await StopTracking();
                        response = "⏹️ Location tracking stopped";
                        break;

                    case "/restart":
                        if (IsServiceRunning())
                        {
                            response = "🔄 Restarting location tracking service...";
                            await SendTelegramMessage(currentSettings.BotToken, currentSettings.ChatId, response);

                            // Stop the current service
                            StopLocationService();

                            // Wait a moment for clean shutdown
                            await Task.Delay(2000);

                            // Start the service again
                            StartLocationService();

                            response = "✅ Location tracking service restarted successfully!";
                        }
                        else
                        {
                            response = "❌ Service is not currently running. Use /start to begin tracking.";
                        }
                        break;

                    case "/token":
                        if (!string.IsNullOrEmpty(param))
                        {
                            var botToken = param;

                            currentSettings.BotToken = botToken;
                            SaveSettings(currentSettings);
                            await SecureStorage.SetAsync("bot_token", botToken);
                            response = "🔑 Bot token updated. Please restart tracking to apply changes.";
                        }
                        else
                        {
                            response = "❌ Please specify a valid bot token";
                        }
                        break;

                    case "/chatid":
                        if (!string.IsNullOrEmpty(param))
                        {
                            var chatId = param;

                            currentSettings.ChatId = chatId;
                            SaveSettings(currentSettings);
                            await SecureStorage.SetAsync("chat_id", chatId);
                            response = "💬 Chat ID updated. Please restart tracking to apply changes.";
                        }
                        else
                        {
                            response = "❌ Please specify a valid chat ID";
                        }
                        break;

                    case "/report":
                        if (!string.IsNullOrEmpty(param) && DateTime.TryParseExact(param, "yyyy-MM-dd", null,
                            System.Globalization.DateTimeStyles.None, out DateTime reportDate))
                        {
                            await SendGeoJsonReport(reportDate, currentSettings);
                            response = $"📊 Generating report for {param}...";
                        }
                        else
                        {
                            response = "❌ Please specify date in YYYY-MM-DD format (e.g., /report 2025-06-26)";
                        }
                        break;

                    case "/today":
                        await SendGeoJsonReport(DateTime.Today, currentSettings);
                        response = "📊 Generating today's report...";
                        break;

                    case "/yesterday":
                        await SendGeoJsonReport(DateTime.Today.AddDays(-1), currentSettings);
                        response = "📊 Generating yesterday's report...";
                        break;

                    case "/files":
                        var availableFiles = geoJsonManager.GetAvailableDataFiles();
                        if (availableFiles.Count > 0)
                        {
                            response = $"📁 Available data files ({availableFiles.Count}):\n";
                            foreach (var file in availableFiles.Take(10))
                            {
                                response += $"• {file}\n";
                            }
                            if (availableFiles.Count > 10)
                            {
                                response += $"... and {availableFiles.Count - 10} more files";
                            }
                        }
                        else
                        {
                            response = "📁 No data files available";
                        }
                        break;

                    case "/cleanup":
                        int keepDays = 30;
                        if (!string.IsNullOrEmpty(param) && int.TryParse(param, out int customDays) && customDays > 0)
                        {
                            keepDays = customDays;
                        }

                        var filesBefore = geoJsonManager.GetAvailableDataFiles().Count;
                        await geoJsonManager.CleanupOldFiles(keepDays);
                        var filesAfter = geoJsonManager.GetAvailableDataFiles().Count;

                        response = $"🧹 Cleanup completed\n" +
                            $"Files before: {filesBefore}\n" +
                            $"Files after: {filesAfter}\n" +
                            $"Deleted: {filesBefore - filesAfter} files older than {keepDays} days";
                        break;
                }

                await SendTelegramMessage(currentSettings.BotToken, currentSettings.ChatId, response);
            }
            catch
            {
                // Silent fail
            }
        }

        private async Task SendGeoJsonReport(DateTime date, AppSettings settings)
        {
            try
            {
                var geoJsonContent = await geoJsonManager.GenerateGeoJsonForDate(date);

                if (string.IsNullOrEmpty(geoJsonContent))
                {
                    await SendTelegramMessage(settings.BotToken, settings.ChatId,
                        $"📊 No location data available for {date:yyyy-MM-dd}");
                    return;
                }

                var geoJson = JsonConvert.DeserializeObject<GeoJsonFeatureCollection>(geoJsonContent);

                var summary = $"📊 **Location Report for {date:yyyy-MM-dd}**\n\n" +
                    $"📍 Total points: {geoJson.Metadata.TotalPoints}\n" +
                    $"📏 Distance traveled: {geoJson.Metadata.DistanceTraveledKm:F2} km\n" +
                    $"⏱️ Tracking duration: {geoJson.Metadata.TrackingDurationHours:F1} hours\n" +
                    $"📱 Device: {geoJson.Metadata.DeviceId}\n" +
                    $"🔢 App version: {geoJson.Metadata.AppVersion}";

                await SendTelegramMessage(settings.BotToken, settings.ChatId, summary);

                var tempFile = Path.Combine(Path.GetTempPath(), $"location_report_{date:yyyy-MM-dd}.geojson");
                await File.WriteAllTextAsync(tempFile, geoJsonContent);

                await SendFileToTelegram(tempFile, $"📊 GeoJSON data for {date:yyyy-MM-dd}", settings);

                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
            catch
            {
                // Silent fail
            }
        }

        private async Task SendFileToTelegram(string filePath, string caption, AppSettings settings)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    return;
                }

                using (var form = new MultipartFormDataContent())
                {
                    var fileBytes = await File.ReadAllBytesAsync(filePath);
                    var fileContent = new ByteArrayContent(fileBytes);
                    fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/geo+json");
                    form.Add(fileContent, "document", Path.GetFileName(filePath));

                    form.Add(new StringContent(settings.ChatId), "chat_id");
                    form.Add(new StringContent(caption), "caption");

                    string apiUrl = $"https://api.telegram.org/bot{settings.BotToken}/sendDocument";
                    var response = await httpClient.PostAsync(apiUrl, form);
                }
            }
            catch
            {
                // Silent fail
            }
        }

        private string MaskToken(string token)
        {
            if (string.IsNullOrEmpty(token) || token.Length < 8)
            {
                return "Not set";
            }

            return token.Substring(0, 8) + "..." + token.Substring(token.Length - 4);
        }

        private async Task SendTelegramMessage(string botToken, string chatId, string message)
        {
            try
            {
                if (string.IsNullOrEmpty(botToken) || string.IsNullOrEmpty(chatId))
                {
                    return;
                }

                string url = $"https://api.telegram.org/bot{botToken}/sendMessage?chat_id={chatId}&text={Uri.EscapeDataString(message)}&parse_mode=Markdown";
                await httpClient.GetStringAsync(url);
            }
            catch
            {
                // Silent fail
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

                    return settings ?? new AppSettings();
                }
                else
                {
                    return new AppSettings();
                }
            }
            catch
            {
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
            catch
            {
                // Silent fail
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

                var preferences = Android.Preferences.PreferenceManager.GetDefaultSharedPreferences(context);
                var editor = preferences.Edit();
                editor.PutBoolean("is_tracking_service_running", true);
                editor.Apply();
            }
            catch
            {
                // Silent fail
            }
        }

        private void StopLocationService()
        {
            try
            {
                var intent = new Intent(context, typeof(BackgroundLocationService));
                context.StopService(intent);

                // Update preference to reflect service stopped
                var preferences = Android.Preferences.PreferenceManager.GetDefaultSharedPreferences(context);
                var editor = preferences.Edit();
                editor.PutBoolean("is_tracking_service_running", false);
                editor.Apply();
            }
            catch
            {
                // Silent fail
            }
        }

        public Task StopTracking()
        {
            try
            {
                var intent = new Intent(context, typeof(BackgroundLocationService));

                BackgroundLocationService.IsStoppingByUserRequest = true;

                var preferences = Android.Preferences.PreferenceManager.GetDefaultSharedPreferences(context);
                var editor = preferences.Edit();
                editor.PutBoolean("is_tracking_service_running", false);
                editor.Apply();

                context.StopService(intent);
                return Task.CompletedTask;
            }
            catch
            {
                return Task.CompletedTask;
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