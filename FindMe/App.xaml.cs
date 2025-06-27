using System;
using Xamarin.Essentials;
using Xamarin.Forms;

namespace FindMe
{
    public partial class App : Application
    {
        // Add this method to your App class in App.xaml.cs
        private async void InitializePasscode()
        {
            try
            {
                // Check if passcode exists
                string savedPasscode = await SecureStorage.GetAsync("settings_passcode");

                // If not, set the default passcode
                if (string.IsNullOrEmpty(savedPasscode))
                {
                    await SecureStorage.SetAsync("settings_passcode", "1234");
                }
            }
            catch (Exception)
            {
                // Handle any secure storage issues
            }
        }

        // And call it from the constructor
        public App()
        {
            InitializeComponent();
            InitializePasscode();

            // Check if setup is completed
            CheckFirstRunAsync();
        }

        private async void CheckFirstRunAsync()
        {
            try
            {
                // Check if setup has been completed
                string setupCompleted = await SecureStorage.GetAsync("setup_completed");
                // Check if passcode is required at startup
                string passcodeAtStartup = await SecureStorage.GetAsync("passcode_at_startup") ?? "true";

                if (string.IsNullOrEmpty(setupCompleted))
                {
                    // First run, show setup page
                    MainPage = new NavigationPage(new FirstRunSetupPage());
                }
                else
                {
                    if (string.IsNullOrEmpty(passcodeAtStartup) || passcodeAtStartup == "true")
                    {
                        // Passcode required, show passcode page first
                        MainPage = new NavigationPage(new PasscodePage(true));
                    }
                    else
                    {
                        // No passcode required, go straight to main page
                        MainPage = new NavigationPage(new MainPage());
                    }
                }
            }
            catch (Exception)
            {
                // If there's an error, assume passcode is required (safer default)
                MainPage = new NavigationPage(new PasscodePage(true));
            }
        }
    }
}