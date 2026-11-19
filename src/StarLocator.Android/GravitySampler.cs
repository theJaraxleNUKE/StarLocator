using System;
using System.Collections.Generic;
using Android.Content;
using Android.Hardware;
using Android.OS;
using Android.Runtime;
using StarLocator.Core;

namespace StarLocator.Droid
{
    /// <summary>
    /// Records accelerometer samples (device frame, m/s^2) with an elapsed-realtime timestamp so
    /// they can be averaged over exactly the camera exposure window.
    /// </summary>
    public sealed class GravitySampler : Java.Lang.Object, ISensorEventListener
    {
        private readonly SensorManager _manager;
        private readonly Sensor? _accel;
        private readonly object _gate = new object();
        private readonly List<(long T, double X, double Y, double Z)> _samples = new List<(long, double, double, double)>();
        private const long KeepNs = 120L * 1_000_000_000L;

        public bool Available => _accel != null;

        public GravitySampler(Context context)
        {
            _manager = context.GetSystemService(Context.SensorService)!.JavaCast<SensorManager>()!;
            _accel = _manager.GetDefaultSensor(SensorType.Accelerometer);
        }

        public void Start()
        {
            if (_accel != null) _manager.RegisterListener(this, _accel, SensorDelay.Game);
        }

        public void Stop() => _manager.UnregisterListener(this);

        public void OnSensorChanged(SensorEvent? e)
        {
            if (e?.Values == null || e.Values.Count < 3) return;
            // Timestamp on receipt (elapsedRealtime) rather than e.Timestamp: the event time base
            // is not guaranteed to match the camera's on every device; delivery latency is ~ms.
            long now = SystemClock.ElapsedRealtimeNanos();
            lock (_gate)
            {
                _samples.Add((now, e.Values[0], e.Values[1], e.Values[2]));
                int drop = 0;
                while (drop < _samples.Count && now - _samples[drop].T > KeepNs) drop++;
                if (drop > 0) _samples.RemoveRange(0, drop);
            }
        }

        public void OnAccuracyChanged(Sensor? sensor, [GeneratedEnum] SensorStatus accuracy) { }

        /// <summary>Latest reading, for the live tilt display.</summary>
        public Vec3? Latest
        {
            get
            {
                lock (_gate)
                {
                    if (_samples.Count == 0) return null;
                    var s = _samples[_samples.Count - 1];
                    return new Vec3(s.X, s.Y, s.Z);
                }
            }
        }

        /// <summary>Mean and RMS deviation of samples whose timestamps fall in [startNs, endNs].</summary>
        public GravityMeasurement? Average(long startNs, long endNs)
        {
            lock (_gate)
            {
                double sx = 0, sy = 0, sz = 0;
                int n = 0;
                foreach (var s in _samples)
                {
                    if (s.T < startNs || s.T > endNs) continue;
                    sx += s.X; sy += s.Y; sz += s.Z; n++;
                }
                if (n == 0) return null;
                var mean = new Vec3(sx / n, sy / n, sz / n);
                double sumSq = 0;
                foreach (var s in _samples)
                {
                    if (s.T < startNs || s.T > endNs) continue;
                    double dx = s.X - mean.X, dy = s.Y - mean.Y, dz = s.Z - mean.Z;
                    sumSq += dx * dx + dy * dy + dz * dz;
                }
                return new GravityMeasurement { Mean = mean, StdDev = Math.Sqrt(sumSq / n), Samples = n };
            }
        }
    }
}
