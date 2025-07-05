using System;
using Android.App;
using Android.Content.PM;
using Android.Runtime;
using Android.OS;

namespace FindMe.Droid
{
    [Activity(Label = "FindMe", Icon = "@mipmap/icon", Theme = "@style/MainTheme", MainLauncher = true, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize)]
    public class MainActivity : global::Xamarin.Forms.Platform.Android.FormsAppCompatActivity
    {
        private TelegramCommandHandler commandHandler;

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            Xamarin.Essentials.Platform.Init(this, savedInstanceState);
            global::Xamarin.Forms.Forms.Init(this, savedInstanceState);

            Plugin.Fingerprint.CrossFingerprint.SetCurrentActivityResolver(() => this);

            LoadApplication(new App());

            commandHandler = new TelegramCommandHandler(this);
            commandHandler.Start();
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            commandHandler?.Stop();
        }

        public override void OnRequestPermissionsResult(int requestCode, string[] permissions, [GeneratedEnum] Android.Content.PM.Permission[] grantResults)
        {
            Xamarin.Essentials.Platform.OnRequestPermissionsResult(requestCode, permissions, grantResults);
            base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        }
    }
}