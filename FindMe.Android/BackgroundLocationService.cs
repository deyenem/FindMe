using Android.App;
using Android.Content;
using Android.OS;
using Android.Locations;
using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Timers;
using AndroidX.Core.App;
using System.IO;
using Newtonsoft.Json;
using FindMe.Models;
using AndroidLocation = Android.Locations.Location;

namespace FindMe.Droid
{
    [Service(ForegroundServiceType = Android.Content.PM.ForegroundService.TypeLocation)]
    public class BackgroundLocationService : Service, ILocationListener
    {
        public static bool IsStoppingByUserRequest = false;

        public const int SERVICE_NOTIFICATION_ID = 1001;
        private const string NOTIFICATION_CHANNEL_ID = "location_service_channel";

        private const int STATIONARY_UPDATE_INTERVAL_MS = 60000;
        private const int MOVING_UPDATE_INTERVAL_MS = 20000;
        private const float SIGNIFICANT_MOVEMENT_METERS = 25;

        private AndroidLocation lastSignificantLocation;
        private int currentUpdateInterval = STATIONARY_UPDATE_INTERVAL_MS;

        private LocationManager locationManager;
        private Timer telegramTimer;
        private Timer dailyGeoJsonTimer;
        private HttpClient httpClient;
        private string currentLocation = "Unknown";
        private PowerManager.WakeLock wakeLock;
        private bool isProcessingLocation = false;

        private GeoJsonManager geoJsonManager;
        private TelegramCommandHandler commandHandler;

        private string telegramBotToken;
        private string chatId;
        private string Interval;

        private readonly string settingsFilePath;
        private IntervalUpdateReceiver intervalUpdateReceiver;

        public BackgroundLocationService()
        {
            settingsFilePath = Path.Combine(System.Environment.GetFolderPath(
                System.Environment.SpecialFolder.Personal), "secure_settings.json");
        }

        public override IBinder OnBind(Intent intent)
        {
            return null;
        }

        private void AcquireWakeLockIfNeeded()
        {
            if (wakeLock == null)
            {
                var powerManager = (PowerManager)GetSystemService(PowerService);
                wakeLock = powerManager.NewWakeLock(
                    WakeLockFlags.Partial,
                    "FindMe::LocationWakeLock");
            }

            if (!wakeLock.IsHeld && isProcessingLocation)
            {
                wakeLock.Acquire(30000);
            }
        }

        private void ReleaseWakeLockIfHeld()
        {
            if (wakeLock != null && wakeLock.IsHeld && !isProcessingLocation)
            {
                wakeLock.Release();
            }
        }

        public override StartCommandResult OnStartCommand(Intent intent, StartCommandFlags flags, int startId)
        {
            var preferences = Android.Preferences.PreferenceManager.GetDefaultSharedPreferences(this);
            var editor = preferences.Edit();
            editor.PutBoolean("is_tracking_service_running", true);
            editor.Apply();

            LoadSettings();
            geoJsonManager = new GeoJsonManager(this);

            // FIXED: Register broadcast receiver for interval updates
            if (intervalUpdateReceiver == null)
            {
                intervalUpdateReceiver = new IntervalUpdateReceiver(this);
                var filter = new IntentFilter("com.findme.UPDATE_INTERVAL");
                RegisterReceiver(intervalUpdateReceiver, filter);
            }

            try
            {
                // FIXED: Only start command handler if not already running
                if (commandHandler == null)
                {
                    commandHandler = new TelegramCommandHandler(this);
                    commandHandler.Start();
                }
            }
            catch
            {
                // Silent fail
            }

            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                var channel = new NotificationChannel(
                    NOTIFICATION_CHANNEL_ID,
                    "Location Service Channel",
                    NotificationImportance.High)
                {
                    Description = "Used for location tracking service"
                };

                var notificationManager = (NotificationManager)GetSystemService(NotificationService);
                notificationManager.CreateNotificationChannel(channel);
            }

            var notification = BuildNotification("Location tracking active", "Getting location...");
            StartForeground(SERVICE_NOTIFICATION_ID, notification);

            httpClient = new HttpClient();

            // FIXED: Initialize telegram timer properly
            InitializeTelegramTimer();

            SetupDailyGeoJsonTimer();

            locationManager = GetSystemService(LocationService) as LocationManager;

            if (CheckSelfPermission(Android.Manifest.Permission.AccessFineLocation) == Android.Content.PM.Permission.Granted)
            {
                try
                {
                    locationManager.RequestLocationUpdates(
                        LocationManager.GpsProvider,
                        currentUpdateInterval,
                        SIGNIFICANT_MOVEMENT_METERS,
                        this);

                    var lastKnownLocation = locationManager.GetLastKnownLocation(LocationManager.GpsProvider);
                    if (lastKnownLocation != null)
                    {
                        lastSignificantLocation = lastKnownLocation;
                        OnLocationChanged(lastKnownLocation);
                    }
                }
                catch (Exception ex)
                {
                    var errorNotification = BuildNotification("Location Error", ex.Message);
                    var notificationManager = NotificationManagerCompat.From(this);
                    notificationManager.Notify(SERVICE_NOTIFICATION_ID, errorNotification);
                }
            }

            return StartCommandResult.Sticky;
        }

        // FIXED: Separate method to initialize telegram timer
        private void InitializeTelegramTimer()
        {
            try
            {
                if (telegramTimer != null)
                {
                    telegramTimer.Stop();
                    telegramTimer.Dispose();
                }

                int intervalMs = int.Parse(Interval);
                telegramTimer = new Timer(intervalMs);
                telegramTimer.Elapsed += TelegramTimer_Elapsed;
                telegramTimer.Start();
            }
            catch
            {
                // Fallback to default interval
                telegramTimer = new Timer(60000);
                telegramTimer.Elapsed += TelegramTimer_Elapsed;
                telegramTimer.Start();
            }
        }

        // FIXED: Method to update interval without restarting service
        public void UpdateInterval(int newIntervalMs)
        {
            try
            {
                Interval = newIntervalMs.ToString();

                // Update settings
                var settings = LoadSettingsFromFile();
                if (settings != null)
                {
                    settings.Interval = Interval;
                    SaveSettingsToFile(settings);
                }

                // Restart telegram timer with new interval
                InitializeTelegramTimer();

                // Update notification
                var notification = BuildNotification("Location tracking active",
                    $"Interval updated to {newIntervalMs}ms - {currentLocation}");
                var notificationManager = NotificationManagerCompat.From(this);
                notificationManager.Notify(SERVICE_NOTIFICATION_ID, notification);
            }
            catch
            {
                // Silent fail
            }
        }

        private void SetupDailyGeoJsonTimer()
        {
            var now = DateTime.Now;
            var nextMidnight = now.Date.AddDays(1);
            var timeUntilMidnight = nextMidnight - now;

            dailyGeoJsonTimer = new Timer(timeUntilMidnight.TotalMilliseconds);
            dailyGeoJsonTimer.Elapsed += DailyGeoJsonTimer_Elapsed;
            dailyGeoJsonTimer.AutoReset = false;
            dailyGeoJsonTimer.Start();
        }

        private async void DailyGeoJsonTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            try
            {
                var yesterday = DateTime.Now.AddDays(-1);
                await SendDailyGeoJsonReport(yesterday);

                await geoJsonManager.CleanupOldFiles(30);

                dailyGeoJsonTimer.Interval = TimeSpan.FromDays(1).TotalMilliseconds;
                dailyGeoJsonTimer.AutoReset = true;
                dailyGeoJsonTimer.Start();
            }
            catch
            {
                // Silent fail
            }
        }

        private async Task SendDailyGeoJsonReport(DateTime date)
        {
            try
            {
                if (string.IsNullOrEmpty(telegramBotToken) || string.IsNullOrEmpty(chatId))
                {
                    return;
                }

                var geoJsonContent = await geoJsonManager.GenerateGeoJsonForDate(date);

                if (string.IsNullOrEmpty(geoJsonContent))
                {
                    await SendMessageToTelegram($"No location data available for {date:yyyy-MM-dd}");
                    return;
                }

                var tempFile = Path.Combine(Path.GetTempPath(), $"location_report_{date:yyyy-MM-dd}.geojson");
                await File.WriteAllTextAsync(tempFile, geoJsonContent);

                await SendFileToTelegram(tempFile, $"Daily location report for {date:yyyy-MM-dd}");

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

        private async Task SendFileToTelegram(string filePath, string caption)
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

                    form.Add(new StringContent(chatId), "chat_id");
                    form.Add(new StringContent(caption), "caption");

                    string apiUrl = $"https://api.telegram.org/bot{telegramBotToken}/sendDocument";
                    var response = await httpClient.PostAsync(apiUrl, form);
                }
            }
            catch
            {
                // Silent fail
            }
        }

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(settingsFilePath))
                {
                    var json = File.ReadAllText(settingsFilePath);
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json);

                    if (!string.IsNullOrEmpty(settings?.BotToken) &&
                        !string.IsNullOrEmpty(settings?.ChatId) &&
                        !string.IsNullOrEmpty(settings?.Interval))
                    {
                        telegramBotToken = settings.BotToken;
                        chatId = settings.ChatId;
                        Interval = settings.Interval;
                        return;
                    }
                }

                telegramBotToken = null;
                chatId = null;
                Interval = "60000";

                var notification = BuildNotification(
                    "Configuration Issue",
                    "Telegram integration disabled. Please set up in app.");
                NotificationManagerCompat.From(this).Notify(SERVICE_NOTIFICATION_ID, notification);
            }
            catch
            {
                telegramBotToken = null;
                chatId = null;
                Interval = "60000";
            }
        }

        // FIXED: Add methods for reading/writing settings
        private AppSettings LoadSettingsFromFile()
        {
            try
            {
                if (File.Exists(settingsFilePath))
                {
                    var json = File.ReadAllText(settingsFilePath);
                    return JsonConvert.DeserializeObject<AppSettings>(json);
                }
            }
            catch
            {
                // Silent fail
            }
            return null;
        }

        private void SaveSettingsToFile(AppSettings settings)
        {
            try
            {
                var json = JsonConvert.SerializeObject(settings);
                File.WriteAllText(settingsFilePath, json);
            }
            catch
            {
                // Silent fail
            }
        }

        private Notification BuildNotification(string title, string text)
        {
            var intent = new Intent(this, typeof(MainActivity));
            intent.SetFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop);
            var pendingIntent = PendingIntent.GetActivity(this, 0, intent, PendingIntentFlags.Immutable);

            var notificationBuilder = new NotificationCompat.Builder(this, NOTIFICATION_CHANNEL_ID)
                .SetContentTitle(title)
                .SetContentText(text)
                .SetSmallIcon(Resource.Color.tooltip_background_dark)
                .SetOngoing(true)
                .SetContentIntent(pendingIntent);

            return notificationBuilder.Build();
        }

        private int updateCounter = 0;
        private async void TelegramTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            try
            {
                isProcessingLocation = true;
                AcquireWakeLockIfNeeded();

                bool credentialsAvailable = !string.IsNullOrEmpty(telegramBotToken) && !string.IsNullOrEmpty(chatId);

                if (credentialsAvailable)
                {
                    if (currentLocation != "Unknown")
                    {
                        bool inMovingMode = currentUpdateInterval == MOVING_UPDATE_INTERVAL_MS;
                        if (inMovingMode || updateCounter % 3 == 0)
                        {
                            await SendLocationToTelegram();
                        }
                        updateCounter++;

                        var notification = BuildNotification("Location tracking active",
                            $"Last update: {DateTime.Now.ToString("HH:mm:ss")} - {currentLocation}");
                        var notificationManager = NotificationManagerCompat.From(this);
                        notificationManager.Notify(SERVICE_NOTIFICATION_ID, notification);
                    }
                    else
                    {
                        await SendMessageToTelegram();
                    }
                }
                else
                {
                    var notification = BuildNotification(
                        "Location tracking active",
                        $"Last update: {DateTime.Now.ToString("HH:mm:ss")} - {currentLocation} (Telegram disabled)");
                    var notificationManager = NotificationManagerCompat.From(this);
                    notificationManager.Notify(SERVICE_NOTIFICATION_ID, notification);
                }
            }
            catch
            {
                // Silent fail
            }
            finally
            {
                isProcessingLocation = false;
                ReleaseWakeLockIfHeld();
            }
        }

        private async Task SendLocationToTelegram()
        {
            try
            {
                if (string.IsNullOrEmpty(telegramBotToken) || string.IsNullOrEmpty(chatId) || string.IsNullOrEmpty(Interval))
                {
                    return;
                }

                string[] coordinates = currentLocation.Split(',');

                if (coordinates.Length != 2)
                {
                    return;
                }

                if (!double.TryParse(coordinates[0], out double latitude) ||
                    !double.TryParse(coordinates[1], out double longitude))
                {
                    return;
                }

                string apiUrl = $"https://api.telegram.org/bot{telegramBotToken}/sendLocation?chat_id={chatId}&latitude={latitude}&longitude={longitude}";

                await httpClient.GetAsync(apiUrl);
            }
            catch
            {
                // Silent fail
            }
        }

        private async Task SendMessageToTelegram(string message = "Location+Unknown!")
        {
            try
            {
                if (string.IsNullOrEmpty(telegramBotToken) || string.IsNullOrEmpty(chatId) || string.IsNullOrEmpty(Interval))
                {
                    return;
                }

                string apiUrlMessage = $"https://api.telegram.org/bot{telegramBotToken}/sendMessage?chat_id={chatId}&text={message}";

                await httpClient.GetAsync(apiUrlMessage);
            }
            catch
            {
                // Silent fail
            }
        }

        public void OnLocationChanged(AndroidLocation location)
        {
            if (location != null)
            {
                try
                {
                    isProcessingLocation = true;
                    AcquireWakeLockIfNeeded();

                    double latitude = location.Latitude;
                    double longitude = location.Longitude;

                    string strlatitude = Convert.ToString(latitude).Replace(',', '.');
                    string strlongitude = Convert.ToString(longitude).Replace(',', '.');

                    currentLocation = $"{strlatitude},{strlongitude}";

                    // Save location data
                    _ = Task.Run(() => {
                        try
                        {
                            geoJsonManager.AddLocationPoint(location, "automatic");
                        }
                        catch
                        {
                            // Silent fail
                        }
                    });

                    // Determine if we should adjust update frequency based on movement
                    if (lastSignificantLocation != null)
                    {
                        float distanceFromLast = location.DistanceTo(lastSignificantLocation);

                        if (distanceFromLast > SIGNIFICANT_MOVEMENT_METERS)
                        {
                            currentUpdateInterval = MOVING_UPDATE_INTERVAL_MS;
                            lastSignificantLocation = location;
                        }
                        else
                        {
                            currentUpdateInterval = STATIONARY_UPDATE_INTERVAL_MS;
                        }

                        if (locationManager != null)
                        {
                            try
                            {
                                locationManager.RemoveUpdates(this);
                                locationManager.RequestLocationUpdates(
                                    LocationManager.GpsProvider,
                                    currentUpdateInterval,
                                    SIGNIFICANT_MOVEMENT_METERS,
                                    this);
                            }
                            catch
                            {
                                // Silent fail
                            }
                        }
                    }
                    else
                    {
                        lastSignificantLocation = location;
                    }
                }
                catch
                {
                    // Silent fail
                }
                finally
                {
                    isProcessingLocation = false;
                    ReleaseWakeLockIfHeld();
                }
            }
        }

        public void OnProviderEnabled(string provider)
        {
            var notification = BuildNotification("Location tracking active", "GPS enabled");
            NotificationManagerCompat.From(this).Notify(SERVICE_NOTIFICATION_ID, notification);
        }

        public void OnProviderDisabled(string provider)
        {
            var notification = BuildNotification("Location tracking active", "GPS disabled");
            NotificationManagerCompat.From(this).Notify(SERVICE_NOTIFICATION_ID, notification);
        }

        public void OnStatusChanged(string provider, Availability status, Bundle extras)
        {
        }

        public override void OnDestroy()
        {
            var preferences = Android.Preferences.PreferenceManager.GetDefaultSharedPreferences(this);
            var editor = preferences.Edit();
            editor.PutBoolean("is_tracking_service_running", false);
            editor.Apply();

            base.OnDestroy();

            // FIXED: Unregister broadcast receiver
            if (intervalUpdateReceiver != null)
            {
                try
                {
                    UnregisterReceiver(intervalUpdateReceiver);
                    intervalUpdateReceiver = null;
                }
                catch
                {
                    // Silent fail
                }
            }

            if (wakeLock != null && wakeLock.IsHeld)
            {
                wakeLock.Release();
                wakeLock = null;
            }

            if (telegramTimer != null)
            {
                telegramTimer.Stop();
                telegramTimer.Dispose();
                telegramTimer = null;
            }

            if (dailyGeoJsonTimer != null)
            {
                dailyGeoJsonTimer.Stop();
                dailyGeoJsonTimer.Dispose();
                dailyGeoJsonTimer = null;
            }

            if (commandHandler != null)
            {
                commandHandler.Stop();
                commandHandler = null;
            }

            if (locationManager != null)
            {
                locationManager.RemoveUpdates(this);
                locationManager = null;
            }

            if (httpClient != null)
            {
                httpClient.Dispose();
                httpClient = null;
            }

            if (!IsStoppingByUserRequest)
            {
                var intent = new Intent(ApplicationContext, typeof(BackgroundLocationService));
                StartService(intent);
            }
        }
    }

    // FIXED: Add broadcast receiver class for interval updates
    public class IntervalUpdateReceiver : BroadcastReceiver
    {
        private readonly BackgroundLocationService service;

        public IntervalUpdateReceiver(BackgroundLocationService service)
        {
            this.service = service;
        }

        public override void OnReceive(Context context, Intent intent)
        {
            if (intent.Action == "com.findme.UPDATE_INTERVAL")
            {
                int newInterval = intent.GetIntExtra("new_interval", 60000);
                service.UpdateInterval(newInterval);
            }
        }
    }
}