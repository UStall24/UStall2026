using System;
using System.Data;
using System.Reflection.Metadata;
using GalaSoft.MvvmLight;
using UStallGUI.ViewModel;

namespace UStallGUI.Model
{
    public class GripperModel() : ObservableObject
    {
        private GripperDataModel _g1s1 = new(50, 0, 100);
        private GripperDataModel _g1s2 = new(50, 0, 100);
        private GripperDataModel _g1s3 = new(50, 0, 36);

        public GripperDataModel G1S1
        {
            get => _g1s1;
            set => Set(ref _g1s1, value);
        }

        public GripperDataModel G1S2
        {
            get => _g1s2;
            set => Set(ref _g1s2, value);
        }

        public GripperDataModel G1S3
        {
            get => _g1s3;
            set => Set(ref _g1s3, value);
        }

        public byte[] GetGripperBytes()
        {
            byte[] value =
            {
                G1S1.GetByte(),
                G1S2.GetByte(),
                G1S3.GetByte(),
                0,
                0,
                0,
                0,
                0
            };
            return value;
        }

        public string MechproGripperExecuteMessage(GripperAssignment assignment)
        {
            string msg = "";
            switch (assignment)
            {
                case GripperAssignment.Gripper2_Servo1Plus:
                    msg = "hg";
                    break;

                case GripperAssignment.Gripper2_Servo1Minus:
                    msg = "rg";
                    break;

                case GripperAssignment.Gripper2_Servo2Plus:
                    msg = "afg";
                    break;

                case GripperAssignment.Gripper2_Servo2Minus:
                    msg = "efg";
                    break;

                case GripperAssignment.Gripper2_PumpOn:
                    msg = "P_ein_G";
                    break;

                case GripperAssignment.Gripper2_DefaultPosition:
                    msg = "GS_G";
                    break;
            }

            return msg;
        }

        public override string ToString()
        {
            return $"G1S1: {G1S1.GetByte()}, G1S2: {G1S2.GetByte()}, G1S3: {G1S3.GetByte()}";
        }
    }

    public class GripperDataModel : ObservableObject
    {
        private int _currentValue;
        private int _lowerLimit;
        private int _higherLimit;

        public GripperDataModel(int currentValue, int lowerLimit, int higherLimit)
        {
            CurrentValue = currentValue;
            _lowerLimit = lowerLimit;
            _higherLimit = higherLimit;
        }

        public int CurrentValue
        {
            get => _currentValue;
            set => Set(ref _currentValue, Math.Clamp(value, 0, 100));
        }

        public int LowerLimit
        {
            get => _lowerLimit;
            set => Set(ref _lowerLimit, Math.Clamp(value, 0, HigherLimit));
        }

        public int HigherLimit
        {
            get => _higherLimit;
            set => Set(ref _higherLimit, Math.Clamp(value, LowerLimit, 100));
        }

        public byte GetByte()
        {
            if (HigherLimit == LowerLimit) return 0; // Avoid division by zero

            double normalized = CurrentValue / 100.0 * (HigherLimit - LowerLimit) + LowerLimit;
            byte value = (byte)(normalized * 2.55);
            return value;
        }
    }
}