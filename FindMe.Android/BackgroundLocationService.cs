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

namespace FindMe.Droid
{
    [Service(ForegroundServiceType = Android.Content.PM.ForegroundService.TypeLocation)]
    public class BackgroundLocationService : Service, ILocationListener
    {

        public static bool IsStoppingByUserRequest = false;

        public const int SERVICE_NOTIFICATION_ID = 1001;
        private const string NOTIFICATION_CHANNEL_ID = "location_service_channel";

        // Improved implementation with adaptive parameters
        private const int STATIONARY_UPDATE_INTERVAL_MS = 60000; // 1 minute when not moving much
        private const int MOVING_UPDATE_INTERVAL_MS = 20000;    // 20 seconds when actively moving
        private const float SIGNIFICANT_MOVEMENT_METERS = 25;    // Consider movement significant at 25m

        private Location lastSignificantLocation;
        private int currentUpdateInterval = STATIONARY_UPDATE_INTERVAL_MS;

        private LocationManager locationManager;
        private Timer telegramTimer;
        private HttpClient httpClient;
        private string currentLocation = "Unknown";
        private PowerManager.WakeLock wakeLock;
        // Add a new flag to track if we're actively processing
        private bool isProcessingLocation = false;

        // Use instance variables instead of constants
        private string telegramBotToken;
        private string chatId;
        private string Interval;

        // Helper for secure storage
        private readonly string settingsFilePath;

        public BackgroundLocationService()
        {
            // Initialize the path for settings file
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
                // Set a timeout to release the wake lock if something goes wrong
                // 30 seconds should be more than enough to process location and send to Telegram
                wakeLock.Acquire(30000); // 30 seconds timeout
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
            // At the start of the method, add:
            var preferences = Android.Preferences.PreferenceManager.GetDefaultSharedPreferences(this);
            var editor = preferences.Edit();
            editor.PutBoolean("is_tracking_service_running", true);
            editor.Apply();

            // Load settings from storage
            LoadSettings();

            // Create notification channel for Android 8.0+
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

            // Create notification for foreground service
            var notification = BuildNotification("Location tracking active", "Getting location...");
            StartForeground(SERVICE_NOTIFICATION_ID, notification);

            // Initialize HTTP client
            httpClient = new HttpClient();

            // Initialize timer
            telegramTimer = new Timer(int.Parse(Interval));
            telegramTimer.Elapsed += TelegramTimer_Elapsed;
            telegramTimer.Start();

            // Initialize location manager
            locationManager = GetSystemService(LocationService) as LocationManager;

            // Request location updates if permission is granted
            if (CheckSelfPermission(Android.Manifest.Permission.AccessFineLocation) == Android.Content.PM.Permission.Granted)
            {
                try
                {
                    // Start with more conservative interval
                    locationManager.RequestLocationUpdates(
                        LocationManager.GpsProvider,
                        currentUpdateInterval,
                        SIGNIFICANT_MOVEMENT_METERS,
                        this);

                    // Also get initial location
                    var lastKnownLocation = locationManager.GetLastKnownLocation(LocationManager.GpsProvider);
                    if (lastKnownLocation != null)
                    {
                        lastSignificantLocation = lastKnownLocation;
                        OnLocationChanged(lastKnownLocation);
                    }
                }
                catch (Exception ex)
                {
                    // Update notification with error
                    var errorNotification = BuildNotification("Location Error", ex.Message);
                    var notificationManager = NotificationManagerCompat.From(this);
                    notificationManager.Notify(SERVICE_NOTIFICATION_ID, errorNotification);
                }
            }

            // Restart if killed
            return StartCommandResult.Sticky;
        }

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(settingsFilePath))
                {
                    var json = File.ReadAllText(settingsFilePath);
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json);

                    // Only use settings if all fields are present
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

                // If we get here, we couldn't load valid settings
                // Instead of hardcoded fallbacks, disable functionality
                telegramBotToken = null;
                chatId = null;
                Interval = "60000"; // Just set a reasonable default interval

                // Log this issue
                Android.Util.Log.Error("BackgroundLocationService",
                    "Failed to load Telegram settings. Telegram updates disabled.");

                // Update notification to inform user
                var notification = BuildNotification(
                    "Configuration Issue",
                    "Telegram integration disabled. Please set up in app.");
                NotificationManagerCompat.From(this).Notify(SERVICE_NOTIFICATION_ID, notification);
            }
            catch (Exception ex)
            {
                // Same as above, disable functionality instead of using hardcoded values
                telegramBotToken = null;
                chatId = null;
                Interval = "60000";

                Android.Util.Log.Error("BackgroundLocationService",
                    $"Error loading settings: {ex.Message}");
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
                //.SetSmallIcon(Resource.Drawable.location_pin) // Use a proper location icon
                .SetSmallIcon(Resource.Color.tooltip_background_dark)
                .SetOngoing(true)
                .SetContentIntent(pendingIntent);

            return notificationBuilder.Build();
        }

        // Add this as a class-level field
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
                    // Existing flow with credentials
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
                    // No credentials available
                    var notification = BuildNotification(
                        "Location tracking active",
                        $"Last update: {DateTime.Now.ToString("HH:mm:ss")} - {currentLocation} (Telegram disabled)");
                    var notificationManager = NotificationManagerCompat.From(this);
                    notificationManager.Notify(SERVICE_NOTIFICATION_ID, notification);
                }
            }
            catch (Exception ex)
            {
                // Log the exception
                Android.Util.Log.Error("TelegramTimer", ex.ToString());
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
                // Check if credentials are available
                if (string.IsNullOrEmpty(telegramBotToken) || string.IsNullOrEmpty(chatId) || string.IsNullOrEmpty(Interval))
                {
                    // Can't send without credentials
                    return;
                }

                // Parse the location text to get latitude and longitude
                string[] coordinates = currentLocation.Split(',');
                //string message = currentLocation.ToString();


                //string apiUrlMessage_ = $"https://api.telegram.org/bot{telegramBotToken}/sendMessage?chat_id={chatId}&&text={message}";

                //// Send the request
                //await httpClient.GetAsync(apiUrlMessage_);

                if (coordinates.Length != 2)
                {
                    return;
                }

                if (!double.TryParse(coordinates[0], out double latitude) ||
                    !double.TryParse(coordinates[1], out double longitude))
                {
                    return;
                }

                // Telegram Bot API endpoint for sending location
                string apiUrl = $"https://api.telegram.org/bot{telegramBotToken}/sendLocation?chat_id={chatId}&latitude={latitude}&longitude={longitude}";
                //string apiUrl = $"https://api.telegram.org/bot{telegramBotToken}/sendLocation?chat_id={chatId}&latitude={strlatitude}&longitude={strlongitude}";


                // Send the request
                await httpClient.GetAsync(apiUrl);
                //await httpClient.GetAsync(apiUrlTest);
            }
            catch (Exception)
            {
                // Silent failure - we'll try again next time
            }
        }

        private async Task SendMessageToTelegram()
        {
            try
            {
                // Check if credentials are available
                if (string.IsNullOrEmpty(telegramBotToken) || string.IsNullOrEmpty(chatId) || string.IsNullOrEmpty(Interval))
                {
                    // Can't send without credentials
                    return;
                }

                string apiUrlMessage = $"https://api.telegram.org/bot{telegramBotToken}/sendMessage?chat_id={chatId}&&text=Location+Unknown!";

                // Send the request
                await httpClient.GetAsync(apiUrlMessage);
            }
            catch (Exception)
            {
                // Silent failure - we'll try again next time
            }
        }

        public void OnLocationChanged(Location location)
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

                    // Adaptive location updating based on movement
                    if (lastSignificantLocation != null)
                    {
                        float distanceInMeters = location.DistanceTo(lastSignificantLocation);

                        // If significant movement detected
                        if (distanceInMeters > SIGNIFICANT_MOVEMENT_METERS)
                        {
                            // Store this as our last significant location
                            lastSignificantLocation = location;

                            // If we weren't already in moving mode, switch to it
                            if (currentUpdateInterval != MOVING_UPDATE_INTERVAL_MS)
                            {
                                currentUpdateInterval = MOVING_UPDATE_INTERVAL_MS;

                                // Update the location request parameters
                                if (CheckSelfPermission(Android.Manifest.Permission.AccessFineLocation) == Android.Content.PM.Permission.Granted)
                                {
                                    locationManager.RemoveUpdates(this);
                                    locationManager.RequestLocationUpdates(
                                        LocationManager.GpsProvider,
                                        currentUpdateInterval,
                                        SIGNIFICANT_MOVEMENT_METERS / 2, // Smaller distance when moving
                                        this);
                                }
                            }
                        }
                        else
                        {
                            // If we've been stationary for a while and were in moving mode
                            if (currentUpdateInterval != STATIONARY_UPDATE_INTERVAL_MS)
                            {
                                // After a certain number of stationary updates, switch to stationary mode
                                // Here you could implement a counter to track consecutive stationary updates
                                currentUpdateInterval = STATIONARY_UPDATE_INTERVAL_MS;

                                // Update the location request parameters
                                if (CheckSelfPermission(Android.Manifest.Permission.AccessFineLocation) == Android.Content.PM.Permission.Granted)
                                {
                                    locationManager.RemoveUpdates(this);
                                    locationManager.RequestLocationUpdates(
                                        LocationManager.GpsProvider,
                                        currentUpdateInterval,
                                        SIGNIFICANT_MOVEMENT_METERS, // Larger distance when stationary
                                        this);
                                }
                            }
                        }
                    }
                    else
                    {
                        lastSignificantLocation = location;
                    }
                }
                finally
                {
                    isProcessingLocation = false;
                    ReleaseWakeLockIfHeld();
                }
            }
        }

        public void OnProviderDisabled(string provider)
        {
            // Update notification to inform user
            var notification = BuildNotification("GPS Disabled", "Please enable GPS for location tracking");
            var notificationManager = NotificationManagerCompat.From(this);
            notificationManager.Notify(SERVICE_NOTIFICATION_ID, notification);
        }

        public void OnProviderEnabled(string provider)
        {
            // GPS is enabled again, update notification
            var notification = BuildNotification("Location tracking active", "GPS enabled, tracking location");
            var notificationManager = NotificationManagerCompat.From(this);
            notificationManager.Notify(SERVICE_NOTIFICATION_ID, notification);
        }

        public void OnStatusChanged(string provider, Availability status, Bundle extras)
        {
            // Handle status changed
        }

        public override void OnDestroy()
        {
            var preferences = Android.Preferences.PreferenceManager.GetDefaultSharedPreferences(this);
            var editor = preferences.Edit();
            editor.PutBoolean("is_tracking_service_running", false);
            editor.Apply();

            base.OnDestroy();

            // Always release wake lock on destroy, regardless of processing state
            if (wakeLock != null && wakeLock.IsHeld)
            {
                wakeLock.Release();
                wakeLock = null;
            }

            // Clean up resources
            if (telegramTimer != null)
            {
                telegramTimer.Stop();
                telegramTimer.Dispose();
                telegramTimer = null;
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

            // Only restart if not explicitly stopped by user
            if (!IsStoppingByUserRequest)
            {
                var intent = new Intent(ApplicationContext, typeof(BackgroundLocationService));
                StartService(intent);
            }
        }
    }
}