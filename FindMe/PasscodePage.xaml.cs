using System;
using System.Threading.Tasks;
using Xamarin.Essentials;
using Xamarin.Forms;
using Plugin.Fingerprint;
using Plugin.Fingerprint.Abstractions;

namespace FindMe
{
    public partial class PasscodePage : ContentPage
    {
        private const string DEFAULT_PASSCODE = "1234"; // Default passcode if none set
        private const string PASSCODE_KEY = "settings_passcode";
        private const string BIOMETRIC_ENABLED_KEY = "biometric_enabled";
        private const int MAX_ATTEMPTS = 5; // Maximum number of failed attempts
        private const int LOCKOUT_MINUTES = 5; // Lockout duration in minutes

        private const string LOCKOUT_UNTIL_KEY = "lockout_until";
        private const string FAILED_ATTEMPTS_KEY = "failed_attempts";

        private string enteredPasscode = "";
        private int failedAttempts = 0;
        private DateTime? lockoutUntil = null;

        private bool isAppStartup;

        public PasscodePage(bool isAppStartup = false)
        {
            InitializeComponent();
            this.isAppStartup = isAppStartup;

            // If it's app startup, hide the "Change Passcode" button
            if (isAppStartup)
            {
                btnChangePasscode.IsVisible = false;
            }

            LoadLockoutState();
            UpdatePasscodeDisplay();
            CheckBiometricAvailability();
        }

        // Add this to PasscodePage.xaml.cs
        private void OnExitClicked(object sender, EventArgs e)
        {
            System.Diagnostics.Process.GetCurrentProcess().Kill();
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            CheckLockoutStatus();
        }

        private async void SaveLockoutState()
        {
            if (lockoutUntil.HasValue)
            {
                await SecureStorage.SetAsync(LOCKOUT_UNTIL_KEY, lockoutUntil.Value.ToString("o"));
            }
            else
            {
                try
                {
                    SecureStorage.Remove(LOCKOUT_UNTIL_KEY);
                }
                catch { }
            }

            await SecureStorage.SetAsync(FAILED_ATTEMPTS_KEY, failedAttempts.ToString());
        }

        private async void LoadLockoutState()
        {
            try
            {
                var lockoutStr = await SecureStorage.GetAsync(LOCKOUT_UNTIL_KEY);
                if (!string.IsNullOrEmpty(lockoutStr) && DateTime.TryParse(lockoutStr, out var lockoutTime))
                {
                    lockoutUntil = lockoutTime;
                }

                var attemptsStr = await SecureStorage.GetAsync(FAILED_ATTEMPTS_KEY);
                if (!string.IsNullOrEmpty(attemptsStr) && int.TryParse(attemptsStr, out var attempts))
                {
                    failedAttempts = attempts;
                }
            }
            catch
            {
                // If there's an error, reset the lockout
                lockoutUntil = null;
                failedAttempts = 0;
            }
        }

        private async void CheckBiometricAvailability()
        {
            try
            {
                // Check if device supports biometrics first
                var biometricAvailable = await CheckBiometricAvailabilityAsync();

                if (biometricAvailable)
                {
                    // Check if user has explicitly enabled or disabled biometrics
                    var isBiometricEnabled = await SecureStorage.GetAsync(BIOMETRIC_ENABLED_KEY);

                    // If not set yet or is set to "true", show the button
                    if (string.IsNullOrEmpty(isBiometricEnabled) || isBiometricEnabled == "true")
                    {
                        btnBiometric.IsVisible = true;
                    }
                    else
                    {
                        btnBiometric.IsVisible = false;
                    }
                }
                else
                {
                    btnBiometric.IsVisible = false;
                }
            }
            catch (Exception ex)
            {
                // Log the exception
                Console.WriteLine($"Biometric check error: {ex}");
                btnBiometric.IsVisible = false;
            }
        }

        private async Task<bool> CheckBiometricAvailabilityAsync()
        {
            try
            {
                var availability = await CrossFingerprint.Current.IsAvailableAsync();
                return availability;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private async void OnBiometricClicked(object sender, EventArgs e)
        {
            try
            {
                var request = new AuthenticationRequestConfiguration(
                    "Authenticate",
                    "Use your fingerprint or face to access app");

                var result = await CrossFingerprint.Current.AuthenticateAsync(request);

                if (result.Authenticated)
                {
                    // Authentication successful
                    if (isAppStartup)
                    {
                        // App is starting - go to MainPage
                        Application.Current.MainPage = new NavigationPage(new MainPage());
                    }
                    else
                    {
                        // Settings access - continue to settings page
                        await Navigation.PushAsync(new SettingsPage());
                    }
                }
            }
            catch (Exception ex)
            {
                await DisplayAlert("Error", $"Biometric authentication error: {ex.Message}", "OK");
            }
        }

        private void CheckLockoutStatus()
        {
            if (lockoutUntil.HasValue && DateTime.Now < lockoutUntil.Value)
            {
                // Still in lockout period
                DisableInput();
                var timeRemaining = (lockoutUntil.Value - DateTime.Now).Minutes + 1;
                lblLockoutMessage.Text = $"Too many failed attempts. Try again in {timeRemaining} minutes.";
                lblLockoutMessage.IsVisible = true;
            }
            else if (lockoutUntil.HasValue)
            {
                // Lockout period has ended
                EnableInput();
                lockoutUntil = null;
                failedAttempts = 0;
                lblLockoutMessage.IsVisible = false;
            }
        }

        private void DisableInput()
        {
            // Disable all input buttons
            foreach (var child in numPadGrid.Children)
            {
                if (child is Button button)
                {
                    button.IsEnabled = false;
                }
            }
            btnDelete.IsEnabled = false;
            btnBiometric.IsEnabled = false;
        }

        private void EnableInput()
        {
            // Enable all input buttons
            foreach (var child in numPadGrid.Children)
            {
                if (child is Button button)
                {
                    button.IsEnabled = true;
                }
            }
            btnDelete.IsEnabled = true;
            btnBiometric.IsEnabled = true;
        }

        private async void OnDigitClicked(object sender, EventArgs e)
        {
            if (enteredPasscode.Length >= 4)
                return;

            Button button = (Button)sender;
            string digit = button.Text;
            enteredPasscode += digit;

            UpdatePasscodeDisplay();

            if (enteredPasscode.Length == 4)
            {
                await Task.Delay(200); // Short delay for visual feedback
                await VerifyPasscode();
            }
        }

        private void OnDeleteClicked(object sender, EventArgs e)
        {
            if (enteredPasscode.Length > 0)
            {
                enteredPasscode = enteredPasscode.Substring(0, enteredPasscode.Length - 1);
                UpdatePasscodeDisplay();
            }
        }

        private void UpdatePasscodeDisplay()
        {
            dot1.TextColor = enteredPasscode.Length >= 1 ? Color.FromHex("#4CAF50") : Color.Gray;
            dot2.TextColor = enteredPasscode.Length >= 2 ? Color.FromHex("#4CAF50") : Color.Gray;
            dot3.TextColor = enteredPasscode.Length >= 3 ? Color.FromHex("#4CAF50") : Color.Gray;
            dot4.TextColor = enteredPasscode.Length >= 4 ? Color.FromHex("#4CAF50") : Color.Gray;
        }

        private async Task VerifyPasscode()
        {
            try
            {
                // Check if we're in lockout period
                if (lockoutUntil.HasValue && DateTime.Now < lockoutUntil.Value)
                {
                    enteredPasscode = "";
                    UpdatePasscodeDisplay();
                    return;
                }

                string savedPasscode = await SecureStorage.GetAsync(PASSCODE_KEY);

                // Use default if no passcode is set
                if (string.IsNullOrEmpty(savedPasscode))
                {
                    savedPasscode = DEFAULT_PASSCODE;
                }

                if (enteredPasscode == savedPasscode)
                {
                    // Correct passcode - reset failed attempts
                    failedAttempts = 0;
                    lblLockoutMessage.IsVisible = false;

                    // Navigate based on context
                    if (isAppStartup)
                    {
                        // App is starting - go to MainPage
                        Application.Current.MainPage = new NavigationPage(new MainPage());
                    }
                    else
                    {
                        // Settings access - continue to settings page
                        await Navigation.PushAsync(new SettingsPage());
                    }

                    enteredPasscode = ""; // Clear for next time
                    UpdatePasscodeDisplay();
                }
                else
                {
                    // Incorrect passcode
                    failedAttempts++;

                    if (failedAttempts >= MAX_ATTEMPTS)
                    {
                        // Implement lockout
                        lockoutUntil = DateTime.Now.AddMinutes(LOCKOUT_MINUTES);
                        SaveLockoutState(); // Save the lockout state
                        await DisplayAlert("Locked Out",
                            $"Too many failed attempts. Try again in {LOCKOUT_MINUTES} minutes.",
                            "OK");
                        DisableInput();
                        lblLockoutMessage.Text = $"Too many failed attempts. Try again in {LOCKOUT_MINUTES} minutes.";
                        lblLockoutMessage.IsVisible = true;
                    }
                    else
                    {
                        // Show remaining attempts
                        int remaining = MAX_ATTEMPTS - failedAttempts;
                        await DisplayAlert("Error",
                            $"Incorrect passcode. {remaining} attempts remaining before lockout.",
                            "Try Again");
                    }

                    enteredPasscode = ""; // Clear the input
                    UpdatePasscodeDisplay();
                }
            }
            catch (Exception ex)
            {
                await DisplayAlert("Error", $"Authentication error: {ex.Message}", "OK");
                enteredPasscode = "";
                UpdatePasscodeDisplay();
            }
        }

        private async void OnChangePasswordClicked(object sender, EventArgs e)
        {
            string currentPasscode = await SecureStorage.GetAsync(PASSCODE_KEY) ?? DEFAULT_PASSCODE;

            string result = await DisplayPromptAsync("Change Passcode",
                "Enter current passcode",
                "Confirm",
                "Cancel",
                maxLength: 4,
                keyboard: Keyboard.Numeric);

            if (result == currentPasscode)
            {
                string newPasscode = await DisplayPromptAsync("New Passcode",
                    "Enter new 4-digit passcode",
                    "Save",
                    "Cancel",
                    maxLength: 4,
                    keyboard: Keyboard.Numeric);

                if (!string.IsNullOrEmpty(newPasscode) && newPasscode.Length == 4)
                {
                    // Confirm passcode for better security
                    string confirmPasscode = await DisplayPromptAsync("Confirm Passcode",
                        "Re-enter your new passcode",
                        "Save",
                        "Cancel",
                        maxLength: 4,
                        keyboard: Keyboard.Numeric);

                    if (newPasscode == confirmPasscode)
                    {
                        await SecureStorage.SetAsync(PASSCODE_KEY, newPasscode);
                        await DisplayAlert("Success", "Passcode changed successfully", "OK");

                        // Ask if user wants to enable biometric authentication
                        bool useBiometrics = await DisplayAlert("Biometric Authentication",
                            "Would you like to enable fingerprint/face authentication for future access?",
                            "Yes", "No");

                        if (useBiometrics)
                        {
                            var biometricAvailable = await CheckBiometricAvailabilityAsync();
                            if (biometricAvailable)
                            {
                                await SecureStorage.SetAsync(BIOMETRIC_ENABLED_KEY, "true");
                                btnBiometric.IsVisible = true;
                            }
                            else
                            {
                                await DisplayAlert("Not Available",
                                    "Biometric authentication is not available on this device.",
                                    "OK");
                            }
                        }
                        else
                        {
                            await SecureStorage.SetAsync(BIOMETRIC_ENABLED_KEY, "false");
                            btnBiometric.IsVisible = false;
                        }
                    }
                    else
                    {
                        await DisplayAlert("Error", "Passcodes do not match", "OK");
                    }
                }
                else if (!string.IsNullOrEmpty(newPasscode))
                {
                    await DisplayAlert("Error", "Passcode must be 4 digits", "OK");
                }
            }
            else if (result != null)
            {
                await DisplayAlert("Error", "Incorrect current passcode", "OK");
            }
        }

        protected override bool OnBackButtonPressed()
        {
            if (isAppStartup)
            {
                // If it's app startup, exit app instead of navigating back
                return false; // Let the system handle it (which will exit)
            }

            // Otherwise use your existing code
            Navigation.PopAsync();
            return true;
        }
    }
}