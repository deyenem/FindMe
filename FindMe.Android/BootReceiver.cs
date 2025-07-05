using Android.App;
using Android.Content;
using Android.OS;
using Android.Preferences;

namespace FindMe.Droid
{
    [BroadcastReceiver(Enabled = true, Exported = true)]
    [IntentFilter(new[] { Intent.ActionBootCompleted })]
    public class BootReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context context, Intent intent)
        {
            if (intent.Action == Intent.ActionBootCompleted)
            {
                var preferences = PreferenceManager.GetDefaultSharedPreferences(context);
                bool wasServiceRunning = preferences.GetBoolean("is_tracking_service_running", false);

                if (wasServiceRunning)
                {
                    var serviceIntent = new Intent(context, typeof(BackgroundLocationService));

                    if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                    {
                        context.StartForegroundService(serviceIntent);
                    }
                    else
                    {
                        context.StartService(serviceIntent);
                    }
                }
            }
        }
    }
}