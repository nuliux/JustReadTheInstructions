using System;
using UnityEngine;

namespace JustReadTheInstructions
{
    public class CameraTelemetry
    {
        private readonly Vessel _vessel;

        public string AltitudeText { get; private set; } = "";
        public string SpeedText { get; private set; } = "";
        public double Altitude { get; private set; }
        public double Speed { get; private set; }

        public CameraTelemetry(Vessel vessel)
        {
            _vessel = vessel ?? throw new ArgumentNullException(nameof(vessel));
        }

        public void Update()
        {
            if (_vessel == null) return;

            Altitude = _vessel.altitude;
            Speed = _vessel.speed;

            UpdateAltitudeText();
            UpdateSpeedText();
        }

        private void UpdateAltitudeText()
        {
            double altKm = Altitude / 1000.0;

            if (Math.Abs(altKm) < 1.0)
                AltitudeText = $"ALT: {Altitude:F0} M";
            else if (Math.Abs(altKm) < 1000.0)
                AltitudeText = $"ALT: {altKm:F1} KM";
            else
                AltitudeText = $"ALT: {(altKm / 1000.0):F1} MM";
        }

        private void UpdateSpeedText()
        {
            double speedKmh = Speed * 3.6;

            if (Math.Abs(speedKmh) < 1.0)
                SpeedText = $"SPD: {Speed:F1} M/S";
            else if (Math.Abs(speedKmh) < 10000.0)
                SpeedText = $"SPD: {speedKmh:F0} KM/H";
            else
                SpeedText = $"SPD: {(Speed / 1000.0):F2} KM/S";
        }

        public string GetFormattedTelemetry()
        {
            return $"{AltitudeText}\n{SpeedText}";
        }
    }
}