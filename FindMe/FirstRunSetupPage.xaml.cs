using System;
using Xamarin.Forms;
using Xamarin.Essentials;
using System.IO;
using Newtonsoft.Json;
using FindMe.Models;

namespace FindMe
{
    public partial class FirstRunSetupPage : ContentPage
    {
        private readonly string settingsFilePath;

        public FirstRunSetupPage()
        {
            InitializeComponent();
            settingsFilePath = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.Personal), "secure_settings.json");
        }

        private async void OnSaveClicked(object sender, EventArgs e)
        {
            try
            {
                // Validate inputs
                if (string.IsNullOrEmpty(entryBotToken.Text))
                {
                    await DisplayAlert("Error", "Please enter a Telegram bot token", "OK");
                    return;
                }

                if (string.IsNullOrEmpty(entryChatId.Text))
                {
                    await DisplayAlert("Error", "Please enter a Telegram chat ID", "OK");
                    return;
                }

                if (string.IsNullOrEmpty(entryInterval.Text) || !int.TryParse(entryInterval.Text, out _))
                {
                    await DisplayAlert("Error", "Please enter a valid interval in milliseconds", "OK");
                    return;
                }

                string botToken = entryBotToken.Text;
                string chatId = entryChatId.Text;
                string interval = entryInterval.Text;

                // Save to SecureStorage
                await SecureStorage.SetAsync("bot_token", botToken);
                await SecureStorage.SetAsync("chat_id", chatId);
                await SecureStorage.SetAsync("Interval", interval);
                await SecureStorage.SetAsync("setup_completed", "true");

                // Also save to file for service access
                var settings = new AppSettings
                {
                    BotToken = botToken,
                    ChatId = chatId,
                    Interval = interval,
                };

                string json = JsonConvert.SerializeObject(settings);
                File.WriteAllText(settingsFilePath, json);

                await DisplayAlert("Success", "Setup completed successfully", "OK");

                // Navigate to main page
                Application.Current.MainPage = new NavigationPage(new MainPage());
            }
            catch (Exception ex)
            {
                await DisplayAlert("Error", "Failed to save settings: " + ex.Message, "OK");
            }
        }
    }
}