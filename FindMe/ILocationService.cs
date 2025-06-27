using System.Threading.Tasks;
using Xamarin.Essentials;

namespace FindMe
{
    public interface ILocationService
    {
        Task StartTracking();
        Task StopTracking();
        Task<Location> GetCurrentLocation();
        // Add to ILocationService.cs
        Task<bool> IsTrackingActive();
    }
}
