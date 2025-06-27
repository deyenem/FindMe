using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xamarin.Essentials;
using Xamarin.Forms;

namespace FindMe
{
    public partial class MainPage : ContentPage
    {
        private bool isServiceRunning = false;
        private ILocationService locationService;

        public MainPage()
        {
            InitializeComponent();
            locationService = DependencyService.Get<ILocationService>();
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            await CheckAndRequestLocationPermissions();

            // Check if the service is running and update UI accordingly
            try
            {
                isServiceRunning = await locationService.IsTrackingActive();
                UpdateUI();
            }
            catch (Exception ex)
            {
                await DisplayAlert("Error", $"Failed to check service status: {ex.Message}", "OK");
            }
        }

        private async Task CheckAndRequestLocationPermissions()
        {
            try
            {
                var status = await CheckPermissionStatus();

                if (status != PermissionStatus.Granted)
                {
                    await RequestLocationPermissions();
                }
            }
            catch (Exception ex)
            {
                await DisplayAlert("Permission Error", ex.Message, "OK");
            }
        }

        private async Task<PermissionStatus> CheckPermissionStatus()
        {
            var locationWhenInUse = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
            var locationAlways = await Permissions.CheckStatusAsync<Permissions.LocationAlways>();

            // Return the most permissive status
            return locationAlways == PermissionStatus.Granted ? locationAlways : locationWhenInUse;
        }

        private async Task RequestLocationPermissions()
        {
            var permissionsToRequest = new List<PermissionStatus>();

            try
            {
                // First request "When In Use" permission
                var locationWhenInUse = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
                if (locationWhenInUse != PermissionStatus.Granted)
                {
                    await DisplayAlert("Permission Required",
                        "Location permission is required for this app to work. Please enable it in settings.",
                        "OK");
                    return;
                }

                // Then request "Always" permission with explanation
                var shouldRequestAlways = await DisplayAlert("Background Location",
                    "This app needs background location access to track your location even when the app is closed. Would you like to grant background location permission?",
                    "Yes", "No");

                if (shouldRequestAlways)
                {
                    var locationAlways = await Permissions.RequestAsync<Permissions.LocationAlways>();
                    if (locationAlways != PermissionStatus.Granted)
                    {
                        await DisplayAlert("Background Location",
                            "Background location access was not granted. The app will only track location when it's open.",
                            "OK");
                    }
                }
            }
            catch (Exception ex)
            {
                await DisplayAlert("Permission Error",
                    "An error occurred while requesting location permissions: " + ex.Message,
                    "OK");
            }
        }

        private async void OnStartServiceClicked(object sender, EventArgs e)
        {
            try
            {
                var permissionStatus = await CheckPermissionStatus();
                if (permissionStatus != PermissionStatus.Granted)
                {
                    await DisplayAlert("Permission Required",
                        "Location permission is required to start tracking. Please grant location permission.",
                        "OK");
                    await CheckAndRequestLocationPermissions();
                    return;
                }

                await locationService.StartTracking();
                isServiceRunning = true;
                UpdateUI();
                await DisplayAlert("Success", "Location tracking started", "OK");
            }
            catch (Exception ex)
            {
                await DisplayAlert("Error", ex.Message, "OK");
            }
        }
        private async void OnStopServiceClicked(object sender, EventArgs e)
        {
            try
            {
                await locationService.StopTracking();
                isServiceRunning = false;
                UpdateUI();
                await DisplayAlert("Success", "Location tracking stopped", "OK");
            }
            catch (Exception ex)
            {
                await DisplayAlert("Error", ex.Message, "OK");
            }
        }

        private async void OnShareClicked(object sender, EventArgs e)
        {
            try
            {
                var location = await locationService.GetCurrentLocation();
                if (location != null)
                {
                    double doublelatitude = location.Latitude;
                    double doublelongitude = location.Longitude;

                    string strlatitude = Convert.ToString(doublelatitude).Replace(',', '.');
                    string strlongitude = Convert.ToString(doublelongitude).Replace(',', '.');

                    await Share.RequestAsync(new ShareTextRequest
                    {
                        //Text = $"https://www.google.com/maps?q={location.Latitude},{location.Longitude}",
                        Text = $"https://www.google.com/maps?q={strlatitude},{strlongitude}",
                        Title = "Share Location"
                    });
                }
                else
                {
                    await DisplayAlert("Error", "Could not get current location", "OK");
                }
            }
            catch (Exception ex)
            {
                await DisplayAlert("Error", ex.Message, "OK");
            }
        }

        private async void OnSettingsClicked(object sender, EventArgs e)
        {
            //await Navigation.PushAsync(new SettingsPage());
            await Navigation.PushAsync(new PasscodePage());
        }

        private void UpdateUI()
        {
            btnStartService.IsEnabled = !isServiceRunning;
            btnStopService.IsEnabled = isServiceRunning;
            btnShare.IsEnabled = true; // Always enable share button, it will get current location when clicked
            txtStatus.Text = $"Service Status: {(isServiceRunning ? "Running" : "Stopped")}";
        }
    }
}