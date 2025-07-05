using Android.Content;
using Android.OS;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using FindMe.Models;
using AndroidLocation = Android.Locations.Location;

namespace FindMe.Droid
{
    public class GeoJsonManager
    {
        private readonly string dataDirectory;
        private readonly string currentDayFile;
        private readonly Context context;
        private readonly object lockObject = new object();

        public GeoJsonManager(Context context)
        {
            this.context = context;
            dataDirectory = Path.Combine(System.Environment.GetFolderPath(
                System.Environment.SpecialFolder.Personal), "LocationData");
            Directory.CreateDirectory(dataDirectory);

            currentDayFile = Path.Combine(dataDirectory, $"locations_{DateTime.Now:yyyy-MM-dd}.json");
        }

        public void AddLocationPoint(AndroidLocation location, string updateType = "automatic")
        {
            try
            {
                lock (lockObject)
                {
                    var locationData = new LocationData
                    {
                        Timestamp = DateTime.UtcNow,
                        Latitude = location.Latitude,
                        Longitude = location.Longitude,
                        Accuracy = location.HasAccuracy ? (float?)location.Accuracy : null,
                        Speed = location.HasSpeed ? (float?)location.Speed : null,
                        Bearing = location.HasBearing ? (float?)location.Bearing : null,
                        Altitude = location.HasAltitude ? (double?)location.Altitude : null,
                        BatteryLevel = GetBatteryLevel(),
                        UpdateType = updateType,
                        SentToTelegram = false
                    };

                    var locations = LoadCurrentDayLocations();
                    locations.Add(locationData);

                    SaveLocationData(locations, currentDayFile);
                }
            }
            catch
            {
                // Silent fail
            }
        }

        private List<LocationData> LoadCurrentDayLocations()
        {
            try
            {
                if (File.Exists(currentDayFile))
                {
                    var json = File.ReadAllText(currentDayFile);
                    return JsonConvert.DeserializeObject<List<LocationData>>(json) ?? new List<LocationData>();
                }
            }
            catch
            {
                // Silent fail
            }
            return new List<LocationData>();
        }

        private void SaveLocationData(List<LocationData> locations, string filePath)
        {
            try
            {
                var json = JsonConvert.SerializeObject(locations, Formatting.Indented);
                File.WriteAllText(filePath, json);
            }
            catch
            {
                // Silent fail
            }
        }

        public async Task<string> GenerateGeoJsonForDate(DateTime date)
        {
            try
            {
                var dateString = date.ToString("yyyy-MM-dd");
                var filePath = Path.Combine(dataDirectory, $"locations_{dateString}.json");

                if (!File.Exists(filePath))
                {
                    return null;
                }

                var locations = JsonConvert.DeserializeObject<List<LocationData>>(File.ReadAllText(filePath));
                if (locations == null || !locations.Any())
                {
                    return null;
                }

                var sortedLocations = locations.OrderBy(l => l.Timestamp).ToList();

                var geoJson = new GeoJsonFeatureCollection();

                geoJson.Metadata = new GeoJsonMetadata
                {
                    DeviceId = GetDeviceId(),
                    AppVersion = GetAppVersion(),
                    TrackingDate = date.Date,
                    TotalPoints = locations.Count,
                    DistanceTraveledKm = CalculateTotalDistance(locations),
                    TrackingDurationHours = CalculateTrackingDuration(locations)
                };

                var startTime = sortedLocations.First().Timestamp;
                var endTime = sortedLocations.Last().Timestamp;

                if (sortedLocations.Count > 1)
                {
                    var pathCoordinates = new List<double[]>();
                    foreach (var location in sortedLocations)
                    {
                        pathCoordinates.Add(new double[] { location.Longitude, location.Latitude });
                    }

                    var pathFeature = new GeoJsonFeature
                    {
                        Geometry = new GeoJsonGeometry
                        {
                            Type = "LineString",
                            Coordinates = pathCoordinates.ToArray()
                        },
                        Properties = new GeoJsonProperties
                        {
                            Name = $"Route {date:yyyy-MM-dd}",
                            Description = $"Complete tracking route<br>" +
                                        $"Start: {startTime:HH:mm:ss}<br>" +
                                        $"End: {endTime:HH:mm:ss}<br>" +
                                        $"Points: {sortedLocations.Count}<br>" +
                                        $"Distance: {CalculateTotalDistance(locations):F2} km<br>" +
                                        $"Duration: {CalculateTrackingDuration(locations):F1} hours",
                            Timestamp = startTime,
                            UpdateType = "path_line",
                            Color = "#FF0000",
                            Folder = "Routes",
                            SequenceNumber = null,
                            TimeLabel = null,
                            ElapsedMinutes = null,
                            Speed = null,
                            Accuracy = null,
                            Bearing = null,
                            Altitude = null,
                            BatteryLevel = null
                        }
                    };

                    geoJson.Features.Add(pathFeature);
                }

                int sequenceNumber = 1;
                foreach (var location in sortedLocations)
                {
                    var elapsedTime = location.Timestamp - startTime;
                    var elapsedMinutes = elapsedTime.TotalMinutes;

                    var timeLabel = location.Timestamp.ToString("HH:mm:ss");
                    var pointName = $"#{sequenceNumber} @ {timeLabel}";

                    var description = $"<b>Point #{sequenceNumber}</b><br>" +
                                    $"Time: {timeLabel}<br>" +
                                    $"Elapsed: {elapsedMinutes:F1} min<br>";

                    if (location.Speed.HasValue)
                        description += $"Speed: {location.Speed:F1} m/s<br>";

                    if (location.Accuracy.HasValue)
                        description += $"Accuracy: {location.Accuracy:F1} m<br>";

                    if (location.BatteryLevel.HasValue && location.BatteryLevel > 0)
                        description += $"Battery: {location.BatteryLevel}%<br>";

                    description += $"Type: {location.UpdateType}";

                    string pointColor = "#0000FF";
                    string pointFolder = "Tracking Points";

                    if (sequenceNumber == 1)
                    {
                        pointColor = "#00FF00";
                        pointFolder = "Start/End Points";
                        pointName += " 🏁";
                    }
                    else if (sequenceNumber == sortedLocations.Count)
                    {
                        pointColor = "#FF0000";
                        pointFolder = "Start/End Points";
                        pointName += " 🎯";
                    }
                    else if (location.UpdateType == "significant_change")
                    {
                        pointColor = "#FFA500";
                        pointFolder = "Movement Points";
                        pointName += " 🏃";
                    }

                    var pointFeature = new GeoJsonFeature
                    {
                        Geometry = new GeoJsonGeometry
                        {
                            Type = "Point",
                            Coordinates = new double[] { location.Longitude, location.Latitude }
                        },
                        Properties = new GeoJsonProperties
                        {
                            Name = pointName,
                            Description = description,
                            Timestamp = location.Timestamp,
                            SequenceNumber = sequenceNumber,
                            TimeLabel = timeLabel,
                            ElapsedMinutes = Math.Round(elapsedMinutes, 1),
                            Accuracy = location.Accuracy,
                            Speed = location.Speed,
                            Bearing = location.Bearing,
                            Altitude = location.Altitude,
                            BatteryLevel = location.BatteryLevel,
                            UpdateType = location.UpdateType,
                            Color = pointColor,
                            Folder = pointFolder
                        }
                    };

                    geoJson.Features.Add(pointFeature);
                    sequenceNumber++;
                }

                return JsonConvert.SerializeObject(geoJson, Formatting.Indented);
            }
            catch
            {
                return null;
            }
        }

        private double CalculateTotalDistance(List<LocationData> locations)
        {
            if (locations.Count < 2) return 0;

            double totalDistance = 0;
            for (int i = 1; i < locations.Count; i++)
            {
                var prev = locations[i - 1];
                var curr = locations[i];

                totalDistance += CalculateDistance(prev.Latitude, prev.Longitude, curr.Latitude, curr.Longitude);
            }

            return totalDistance / 1000;
        }

        private double CalculateDistance(double lat1, double lon1, double lat2, double lon2)
        {
            const double R = 6371000;
            var φ1 = lat1 * Math.PI / 180;
            var φ2 = lat2 * Math.PI / 180;
            var Δφ = (lat2 - lat1) * Math.PI / 180;
            var Δλ = (lon2 - lon1) * Math.PI / 180;

            var a = Math.Sin(Δφ / 2) * Math.Sin(Δφ / 2) +
                    Math.Cos(φ1) * Math.Cos(φ2) *
                    Math.Sin(Δλ / 2) * Math.Sin(Δλ / 2);
            var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

            return R * c;
        }

        private double CalculateTrackingDuration(List<LocationData> locations)
        {
            if (locations.Count < 2) return 0;

            var firstLocation = locations.OrderBy(l => l.Timestamp).First();
            var lastLocation = locations.OrderBy(l => l.Timestamp).Last();

            return (lastLocation.Timestamp - firstLocation.Timestamp).TotalHours;
        }

        private int GetBatteryLevel()
        {
            try
            {
                if (Build.VERSION.SdkInt >= BuildVersionCodes.Lollipop)
                {
                    var batteryManager = (BatteryManager)context.GetSystemService(Context.BatteryService);
                    return batteryManager.GetIntProperty((int)BatteryProperty.Capacity);
                }
                else
                {
                    var filter = new IntentFilter(Intent.ActionBatteryChanged);
                    var batteryStatus = context.RegisterReceiver(null, filter);
                    if (batteryStatus != null)
                    {
                        int level = batteryStatus.GetIntExtra(BatteryManager.ExtraLevel, -1);
                        int scale = batteryStatus.GetIntExtra(BatteryManager.ExtraScale, -1);
                        return (int)((level / (float)scale) * 100);
                    }
                }
            }
            catch
            {
                // Silent fail
            }
            return -1;
        }

        private string GetDeviceId()
        {
            return Android.Provider.Settings.Secure.GetString(
                context.ContentResolver, Android.Provider.Settings.Secure.AndroidId);
        }

        private string GetAppVersion()
        {
            try
            {
                var packageInfo = context.PackageManager.GetPackageInfo(context.PackageName, 0);
                return packageInfo.VersionName;
            }
            catch
            {
                return "Unknown";
            }
        }

        public List<string> GetAvailableDataFiles()
        {
            try
            {
                return Directory.GetFiles(dataDirectory, "locations_*.json")
                    .Select(Path.GetFileName)
                    .OrderByDescending(f => f)
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        public async Task CleanupOldFiles(int keepDays = 30)
        {
            try
            {
                var cutoffDate = DateTime.Now.AddDays(-keepDays);
                var files = Directory.GetFiles(dataDirectory, "locations_*.json");

                foreach (var file in files)
                {
                    var fileName = Path.GetFileNameWithoutExtension(file);
                    if (fileName.StartsWith("locations_") &&
                        DateTime.TryParseExact(fileName.Substring(10), "yyyy-MM-dd", null,
                        System.Globalization.DateTimeStyles.None, out var fileDate))
                    {
                        if (fileDate < cutoffDate)
                        {
                            File.Delete(file);
                        }
                    }
                }
            }
            catch
            {
                // Silent fail
            }
        }
    }
}