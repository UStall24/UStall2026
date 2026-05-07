using GalaSoft.MvvmLight;
using System;
using System.Collections.Generic;
using System.Linq;

namespace UStallGUI.Model
{
    public enum DOF
    {
        ForwardsBackwards = 0,
        LeftRight = 1,
        UpDown = 2,
        Yaw = 3,
        Roll = 4,
        Pitch = 5
    }

    public class DirectionValues
    {
        public Dictionary<DOF, MotorValues> Values { get; set; }

        public DirectionValues()
        {
            Values = new Dictionary<DOF, MotorValues>();
            foreach (DOF dof in Enum.GetValues(typeof(DOF)))
                Values[dof] = MotorValues.GetDefaultMotorValues();
        }

        public void UpdateSingleDOF(DOF dof, float[] values)
        {
            Values[dof].UpdateAsArray(values);
        }

        public MotorValues Get(DOF dof) => Values[dof];

        public static DirectionValues GetDefaultDirectionValues()
        {
            return new DirectionValues();
        }

        public static DirectionValues GetInvalidDirectionValues()
        {
            var dv = new DirectionValues();
            foreach (var key in dv.Values.Keys.ToList())
                dv.Values[key] = MotorValues.GetInvalidMotorValues();
            return dv;
        }

        public bool HasInvalidData()
        {
            return Values.Values.Any(v => v.GetAsArray().Any(x => x < -1f || x > 1f));
        }
    }

    public class MotorValues : ObservableObject
    {
        private float[] _values = new float[6];

        public float M1 { get => _values[0]; set => _values[0] = Clip(value); }
        public float M2 { get => _values[1]; set => _values[1] = Clip(value); }
        public float M3 { get => _values[2]; set => _values[2] = Clip(value); }
        public float M4 { get => _values[3]; set => _values[3] = Clip(value); }
        public float M5 { get => _values[4]; set => _values[4] = Clip(value); }
        public float M6 { get => _values[5]; set => _values[5] = Clip(value); }

        public static float Clip(float v) => Math.Clamp(v, -1f, 1f);

        public MotorValues(float[] arr) => UpdateAsArray(arr);

        public float[] GetAsArray() => (float[])_values.Clone();

        public void UpdateAsArray(float[] arr)
        {
            if (arr != null)
                Array.Copy(arr, _values, Math.Min(arr.Length, 6));
        }

        public byte[] GetAsByte()
        {
            byte[] payload = new byte[6];
            for (int i = 0; i < 6; i++)
                payload[i] = (byte)(_values[i] * 127.5 + 127.5);
            return payload;
        }

        public static MotorValues GetDefaultMotorValues()
            => new MotorValues(new float[] { 0, 0, 0, 0, 0, 0 });

        public static MotorValues GetInvalidMotorValues()
            => new MotorValues(new float[] { -100, -100, -100, -100, -100, -100 });
    }
}