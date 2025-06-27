using System;
using Xamarin.Forms;
using Xamarin.Essentials;
using System.IO;
using Newtonsoft.Json;
using System.Threading.Tasks;
using FindMe.Models;

namespace FindMe
{
    public partial class SettingsPage : ContentPage
    {
        private readonly string settingsFilePath;

        public SettingsPage()
        {
            InitializeComponent();

            // Initialize settings file path
            settingsFilePath = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.Personal), "secure_settings.json");

            LoadSettings();
        }

        private const string PASSCODE_STARTUP_KEY = "passcode_at_startup";
        private const string BIOMETRIC_ENABLED_KEY = "biometric_enabled";

        private async void LoadSettings()
        {
            try
            {
                // Load from secure storage without hardcoded fallbacks
                var botToken = await SecureStorage.GetAsync("bot_token") ?? string.Empty;
                var chatId = await SecureStorage.GetAsync("chat_id") ?? string.Empty;
                var interval = await SecureStorage.GetAsync("Interval") ?? "60000"; // Default to 1 minute

                // Fill text fields
                entryBotToken.Text = botToken;
                entryChatId.Text = chatId;
                entryInterval.Text = interval;

                // If secure storage doesn't have values, try loading from file
                if ((string.IsNullOrEmpty(botToken) || string.IsNullOrEmpty(chatId)) && File.Exists(settingsFilePath))
                {
                    var json = File.ReadAllText(settingsFilePath);
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json);

                    if (settings != null)
                    {
                        botToken = string.IsNullOrEmpty(botToken) ? settings.BotToken : botToken;
                        chatId = string.IsNullOrEmpty(chatId) ? settings.ChatId : chatId;
                        interval = string.IsNullOrEmpty(interval) ? settings.Interval : interval;

                        // Update entry fields with file values
                        entryBotToken.Text = botToken;
                        entryChatId.Text = chatId;
                        entryInterval.Text = interval;
                    }
                }

                // Update display text
                txtTokenID.Text = string.IsNullOrEmpty(botToken) ? "Not configured" : botToken;
                txtChatID.Text = string.IsNullOrEmpty(chatId) ? "Not configured" : chatId;
                txtInterval.Text = interval;

                // If valid settings are found, save them to ensure consistency
                if (!string.IsNullOrEmpty(botToken) && !string.IsNullOrEmpty(chatId))
                {
                    // Save to file for service access
                    await SaveToFileAsync(botToken, chatId, interval);
                }
                else
                {
                    // Show a message to the user that they need to configure settings
                    await DisplayAlert("Configuration Needed",
                        "Please enter your Telegram bot token and chat ID to enable location sharing.",
                        "OK");
                }

                // Load security settings
                var passcodeAtStartup = await SecureStorage.GetAsync(PASSCODE_STARTUP_KEY) ?? "true";
                var biometricEnabled = await SecureStorage.GetAsync(BIOMETRIC_ENABLED_KEY) ?? "false";

                // Update switches
                switchPasscodeAtStartup.IsToggled = passcodeAtStartup == "true";
                switchBiometricAuth.IsToggled = biometricEnabled == "true";

                // If device doesn't support biometrics, disable the switch
                bool biometricAvailable = await CheckBiometricAvailabilityAsync();
                switchBiometricAuth.IsEnabled = biometricAvailable;

                if (!biometricAvailable && switchBiometricAuth.IsVisible)
                {
                    // Show a note that biometrics are not available on this device
                    lblBiometricNotAvailable.IsVisible = true;
                }
            }
            catch (Exception ex)
            {
                await DisplayAlert("Error", "Failed to load settings: " + ex.Message, "OK");
            }
        }

        private async Task<bool> CheckBiometricAvailabilityAsync()
        {
            try
            {
                var availability = await Plugin.Fingerprint.CrossFingerprint.Current.IsAvailableAsync();
                return availability;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Add event handlers for the switches
        private async void OnPasscodeAtStartupToggled(object sender, ToggledEventArgs e)
        {
            try
            {
                await SecureStorage.SetAsync(PASSCODE_STARTUP_KEY, e.Value ? "true" : "false");
            }
            catch (Exception ex)
            {
                await DisplayAlert("Error", $"Failed to save passcode setting: {ex.Message}", "OK");
                // Revert switch state on error
                switchPasscodeAtStartup.IsToggled = !e.Value;
            }
        }

        private async void OnBiometricAuthToggled(object sender, ToggledEventArgs e)
        {
            try
            {
                // If enabling biometrics, check if it's available first
                if (e.Value)
                {
                    bool biometricAvailable = await CheckBiometricAvailabilityAsync();
                    if (!biometricAvailable)
                    {
                        await DisplayAlert("Not Available",
                            "Biometric authentication is not available on this device.",
                            "OK");
                        switchBiometricAuth.IsToggled = false;
                        return;
                    }
                }

                await SecureStorage.SetAsync(BIOMETRIC_ENABLED_KEY, e.Value ? "true" : "false");
            }
            catch (Exception ex)
            {
                await DisplayAlert("Error", $"Failed to save biometric setting: {ex.Message}", "OK");
                // Revert switch state on error
                switchBiometricAuth.IsToggled = !e.Value;
            }
        }

        private async void OnSaveClicked(object sender, EventArgs e)
        {
            try
            {
                string botToken = entryBotToken.Text ?? "8121597492:AAHbBFLY46yuYmugh6SZj8W1pL3WFA5935w";
                string chatId = entryChatId.Text ?? "1680946472";
                string Interval = entryInterval.Text ?? "10000";

                // Save to SecureStorage
                await SecureStorage.SetAsync("bot_token", botToken);
                await SecureStorage.SetAsync("chat_id", chatId);
                await SecureStorage.SetAsync("Interval", Interval);

                if(string.IsNullOrEmpty(botToken) || string.IsNullOrEmpty(chatId))
                {
                    await SecureStorage.SetAsync("setup_completed", "");
                }

                // Also save to file for service access
                await SaveToFileAsync(botToken, chatId, Interval);

                await DisplayAlert("Success", "Settings saved successfully", "OK");
                //await Navigation.PopAsync();
                await Navigation.PushAsync(new MainPage());
            }
            catch (Exception ex)
            {
                await DisplayAlert("Error", "Failed to save settings: " + ex.Message, "OK");
            }
        }

        private async Task SaveToFileAsync(string botToken, string chatId, string Interval)
        {
            try
            {
                var settings = new AppSettings
                {
                    BotToken = botToken,
                    ChatId = chatId,
                    Interval = Interval,
                };

                string json = JsonConvert.SerializeObject(settings);
                File.WriteAllText(settingsFilePath, json);
            }
            catch (Exception ex)
            {
                await DisplayAlert("Error", "Failed to save settings to file: " + ex.Message, "OK");
            }
        }
    }
}