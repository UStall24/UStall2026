using GalaSoft.MvvmLight;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace UStallGUI.Model
{
    public class DirectionValues
    {
        public AxisValues Translation { get; set; }
        public AxisValues Rotation { get; set; }

        public void UpdateSingleAxis(Direction dir, Axis axis, float[] values)
        {
            if (dir == Direction.Translation)
            {
                Translation.UpdateAxis(values, axis);
            }
            else
            {
                Rotation.UpdateAxis(values, axis);
            }
        }

        public void SwitchMotors(int m1, int m2)
        {
            Translation.SwitchMotors(m1, m2);
            Rotation.SwitchMotors(m1, m2);
        }

        public static DirectionValues GetDefaultDirectionValues()
        {
            var value = new DirectionValues();
            value.Translation = AxisValues.GetDefaultAxisValues();
            value.Rotation = AxisValues.GetDefaultAxisValues();
            return value;
        }
    }

    public enum Axis
    {
        Z_Axis,
        X_Axis,
        Y_Axis
    }

    public enum Direction
    {
        Translation,
        Rotation
    }

    public class MotorValues : ObservableObject
    {
        private float[] _values = new float[6];

        public float VM1 { get => _values[0]; set => _values[0] = ClipValue(value); }
        public float VM2 { get => _values[1]; set => _values[1] = ClipValue(value); }
        public float VM3 { get => _values[2]; set => _values[2] = ClipValue(value); }
        public float HM1 { get => _values[3]; set => _values[3] = ClipValue(value); }
        public float HM2 { get => _values[4]; set => _values[4] = ClipValue(value); }
        public float HM3 { get => _values[5]; set => _values[5] = ClipValue(value); }

        private static float ClipValue(float value) => Math.Clamp(value, -1f, 1f);

        public MotorValues(float[] array) => UpdateAsArray(array);

        public float[] GetAsArray() => (float[])_values.Clone();

        public void UpdateAsArray(float[] array)
        {
            if (array != null) Array.Copy(array, _values, Math.Min(array.Length, _values.Length));
        }

        public byte[] GetAsByte()
        {
            byte[] payload = new byte[6];
            for (int i = 0; i < 6; i++)
            {
                payload[i] = (byte)(_values[i] * 127.5 + 127.5);
            }
            return payload;
        }

        public void SwitchMotors(int m1, int m2) => (_values[m1 - 1], _values[m2 - 1]) = (_values[m2 - 1], _values[m1 - 1]);

        public static MotorValues GetDefaultMotorValues() => new MotorValues(new float[] { 0.7f, 0.5f, 0.3f, -0.3f, -0.5f, -0.7f });

        public override string ToString() => $"VM1: {VM1}, VM2: {VM2}, VM3: {VM3}, HM1: {HM1}, HM2: {HM2}, HM3: {HM3}";
    }

    public class AxisValues
    {
        public MotorValues Z_Axis { get; set; }
        public MotorValues X_Axis { get; set; }
        public MotorValues Y_Axis { get; set; }

        public void UpdateAxis(float[] array, Axis axis)
        {
            if (axis == Axis.Z_Axis)
            {
                Z_Axis.UpdateAsArray(array);
            }
            else if (axis == Axis.Y_Axis)
            {
                Y_Axis.UpdateAsArray(array);
            }
            else if (axis == Axis.X_Axis)
            {
                X_Axis.UpdateAsArray(array);
            }
        }

        public void SwitchMotors(int m1, int m2)
        {
            Z_Axis.SwitchMotors(m1, m2);
            Y_Axis.SwitchMotors(m1, m2);
            X_Axis.SwitchMotors(m1, m2);
        }

        public static AxisValues GetDefaultAxisValues()
        {
            var value = new AxisValues();
            value.X_Axis = MotorValues.GetDefaultMotorValues();
            value.Y_Axis = MotorValues.GetDefaultMotorValues();
            value.Z_Axis = MotorValues.GetDefaultMotorValues();
            return value;
        }
    }
}