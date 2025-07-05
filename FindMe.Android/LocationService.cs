using Android.OS;
using System.Threading.Tasks;
using Android.Content;
using Xamarin.Forms;
using Android.App;
using Xamarin.Essentials;
using System;
using Application = Android.App.Application;

[assembly: Dependency(typeof(FindMe.Droid.LocationService))]
namespace FindMe.Droid
{
    public class LocationService : ILocationService
    {
        private readonly Context context;
        private Intent serviceIntent;

        public LocationService()
        {
            context = Application.Context;
            serviceIntent = new Intent(context, typeof(BackgroundLocationService));
        }

        public Task StartTracking()
        {
            try
            {
                if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                {
                    context.StartForegroundService(serviceIntent);
                }
                else
                {
                    context.StartService(serviceIntent);
                }

                var preferences = Android.Preferences.PreferenceManager.GetDefaultSharedPreferences(context);
                var editor = preferences.Edit();
                editor.PutBoolean("is_tracking_service_running", true);
                editor.Apply();

                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                return Task.FromException(ex);
            }
        }

        public Task StopTracking()
        {
            try
            {
                BackgroundLocationService.IsStoppingByUserRequest = true;

                var preferences = Android.Preferences.PreferenceManager.GetDefaultSharedPreferences(context);
                var editor = preferences.Edit();
                editor.PutBoolean("is_tracking_service_running", false);
                editor.Apply();

                context.StopService(serviceIntent);
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

        public async Task<Location> GetCurrentLocation()
        {
            try
            {
                var location = await Geolocation.GetLastKnownLocationAsync();
                if (location == null)
                {
                    var request = new GeolocationRequest(GeolocationAccuracy.Medium, TimeSpan.FromSeconds(5));
                    location = await Geolocation.GetLocationAsync(request);
                }
                return location;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public Task<bool> IsTrackingActive()
        {
            var preferences = Android.Preferences.PreferenceManager.GetDefaultSharedPreferences(context);
            bool isRunning = preferences.GetBoolean("is_tracking_service_running", false);
            return Task.FromResult(isRunning);
        }
    }
}