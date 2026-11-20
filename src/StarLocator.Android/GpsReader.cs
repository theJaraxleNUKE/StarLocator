using System;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Locations;
using Android.OS;
using Android.Runtime;

namespace StarLocator.Droid
{
    /// <summary>A single GPS reading used ONLY as the reference for the one-time calibration.</summary>
    public sealed class GpsFix
    {
        public double Latitude { get; init; }
        public double Longitude { get; init; }
        public double AccuracyM { get; init; }
        /// <summary>GPS (UTC) time minus the phone clock, in seconds, at the moment of the fix.</summary>
        public double ClockOffsetS { get; init; }
    }

    /// <summary>
    /// Waits for one fresh GPS fix (accuracy at or below the target, or the best one when time runs out)
    /// and then stops listening. Never used to compute the star fix itself.
    /// </summary>
    public static class GpsReader
    {
        public static async Task<GpsFix> GetFixAsync(Context context, double targetAccuracyM, TimeSpan timeout,
                                                     Action<string> progress, CancellationToken token)
        {
            var lm = context.GetSystemService(Context.LocationService)!.JavaCast<LocationManager>()!;
            if (!lm.IsProviderEnabled(LocationManager.GpsProvider))
                throw new InvalidOperationException("GPS is switched off. Turn on Location in the phone's settings.");

            var tcs = new TaskCompletionSource<Location>(TaskCreationOptions.RunContinuationsAsynchronously);
            var listener = new Listener(targetAccuracyM, tcs, progress);
            lm.RequestLocationUpdates(LocationManager.GpsProvider, 0L, 0f, listener, Looper.MainLooper);
            try
            {
                Task finished = await Task.WhenAny(tcs.Task, Task.Delay(timeout, token));
                token.ThrowIfCancellationRequested();
                Location? loc = finished == tcs.Task ? tcs.Task.Result : listener.Best;
                if (loc == null) throw new TimeoutException("No GPS fix. Go outdoors with a clear view of the sky and try again.");

                // System UTC at the moment of the fix, using the monotonic clock to remove delivery delay.
                long ageNs = SystemClock.ElapsedRealtimeNanos() - loc.ElapsedRealtimeNanos;
                DateTime systemAtFix = DateTime.UtcNow - TimeSpan.FromTicks(ageNs / 100);
                DateTime gpsTime = DateTime.UnixEpoch.AddMilliseconds(loc.Time);
                return new GpsFix
                {
                    Latitude = loc.Latitude,
                    Longitude = loc.Longitude,
                    AccuracyM = loc.HasAccuracy ? loc.Accuracy : double.NaN,
                    ClockOffsetS = (gpsTime - systemAtFix).TotalSeconds
                };
            }
            finally
            {
                lm.RemoveUpdates(listener);
            }
        }

        private sealed class Listener : Java.Lang.Object, ILocationListener
        {
            private readonly double _target;
            private readonly TaskCompletionSource<Location> _tcs;
            private readonly Action<string> _progress;
            public Location? Best { get; private set; }

            public Listener(double target, TaskCompletionSource<Location> tcs, Action<string> progress)
            {
                _target = target; _tcs = tcs; _progress = progress;
            }

            public void OnLocationChanged(Location location)
            {
                if (!location.HasAccuracy) return;
                if (Best == null || location.Accuracy < Best.Accuracy) Best = location;
                _progress($"GPS accuracy {location.Accuracy:F0} m (waiting for ≤ {_target:F0} m)…");
                if (location.Accuracy <= _target) _tcs.TrySetResult(location);
            }

            public void OnProviderDisabled(string provider) { }
            public void OnProviderEnabled(string provider) { }
            public void OnStatusChanged(string? provider, [GeneratedEnum] Availability status, Bundle? extras) { }
        }
    }
}
