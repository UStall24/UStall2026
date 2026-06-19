using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.Command;
using System;
using UStallGUI.Helpers;

namespace UStallGUI.ViewModel
{
    public class MechProAViewModel : ObservableObject
    {
        private readonly UsbCANWrapper can;

        // ================= CAN IDs =================
        private const ushort GRIPPER_J2_ID_SEND = 0x302;

        private const ushort GRIPPER_J3_ID_SEND = 0x303;

        private const ushort GRIPPER_J2_J3_ID_RECV = 0x4B2;

        public MechProAViewModel(UsbCANWrapper can)
        {
            this.can = can;
            GripperCommand = new RelayCommand<string>(ExecuteGripperCommand);

            if (this.can != null)
            {
                // Register the single feedback ID that contains data for both joints
                this.can.RegisterCallback(GRIPPER_J2_J3_ID_RECV, ProcessIncomingCanMessage);
            }
        }

        // ================= COMMAND =================
        public RelayCommand<string> GripperCommand { get; }

        // ================= UI STATE (TARGETS & STEP SIZES) =================

        // J2 UI State
        private int _joint2Position = 0;

        public int Joint2Position
        {
            get => _joint2Position;
            set => Set(ref _joint2Position, value);
        }

        private int _joint2Speed = 5; // Used internally as Step Size

        public int Joint2Speed
        {
            get => _joint2Speed;
            set => Set(ref _joint2Speed, value);
        }

        // J3 UI State
        private int _joint3Position = 0; // Default target

        public int Joint3Position
        {
            get => _joint3Position;
            set => Set(ref _joint3Position, value);
        }

        private int _joint3Speed = 5; // Used internally as Step Size

        public int Joint3Speed
        {
            get => _joint3Speed;
            set => Set(ref _joint3Speed, value);
        }

        // ================= ACTUAL HARDWARE FEEDBACK =================

        private int _actualJoint2Position = 0;

        public int ActualJoint2Position
        {
            get => _actualJoint2Position;
            set => Set(ref _actualJoint2Position, value);
        }

        private int _actualJoint3Position = 0;

        public int ActualJoint3Position
        {
            get => _actualJoint3Position;
            set => Set(ref _actualJoint3Position, value);
        }

        private string _joint2and3Temperature = "0 °C";

        public string Joint2and3Temperature
        {
            get => _joint2and3Temperature;
            set => Set(ref _joint2and3Temperature, value);
        }

        private string _joint2and3Humidity = "0 %";

        public string Joint2and3Humidity
        {
            get => _joint2and3Humidity;
            set => Set(ref _joint2and3Humidity, value);
        }

        // ================= CAN RECEIVE LOGIC =================

        private void ProcessIncomingCanMessage(ushort rxId, byte[] payload)
        {
            if (rxId == GRIPPER_J2_J3_ID_RECV)
            {
                // Assuming a 6-byte combined payload: [J2_H, J2_L, J3_H, J3_L, Temp, Hum]
                if (payload.Length >= 6)
                {
                    ActualJoint2Position = (payload[0] << 8) | payload[1];
                    ActualJoint3Position = (payload[2] << 8) | payload[3];
                    Joint2and3Temperature = $"{payload[4]} °C";
                    Joint2and3Humidity = $"{payload[5]} %";
                }
                // Fallback: If it's only 4 bytes (e.g., [J2_H, J2_L, J3_H, J3_L] with no Temp/Hum)
                else if (payload.Length >= 4)
                {
                    ActualJoint2Position = (payload[0] << 8) | payload[1];
                    ActualJoint3Position = (payload[2] << 8) | payload[3];
                }
            }
        }

        // ================= COMMAND LOGIC =================

        private void ExecuteGripperCommand(string cmd)
        {
            switch (cmd)
            {
                // J2 Commands using Joint2Speed as Step
                case "J2_INC":
                    Joint2Position += Joint2Speed;
                    SendGripper(GRIPPER_J2_ID_SEND, Joint2Position);
                    break;

                case "J2_DEC":
                    Joint2Position -= Joint2Speed;
                    SendGripper(GRIPPER_J2_ID_SEND, Joint2Position);
                    break;

                // J3 Commands using Joint3Speed as Step
                case "J3_INC":
                    Joint3Position += Joint3Speed;
                    SendGripper(GRIPPER_J3_ID_SEND, Joint3Position);
                    break;

                case "J3_DEC":
                    Joint3Position -= Joint3Speed;
                    SendGripper(GRIPPER_J3_ID_SEND, Joint3Position);
                    break;
            }
        }

        // ================= CAN PACKING =================

        private void SendGripper(ushort canId, int targetPosition)
        {
            // Clamp symmetrically around 0. Adjust these limits based on your physical hardware limits!
            targetPosition = Math.Clamp(targetPosition, -90, 90);

            // Reflect clamped position back to UI Target
            if (canId == GRIPPER_J2_ID_SEND) Joint2Position = targetPosition;
            if (canId == GRIPPER_J3_ID_SEND) Joint3Position = targetPosition;

            // Cast to short (Int16) so negative numbers pack correctly into bytes
            short signedPos = (short)targetPosition;

            byte high = (byte)((signedPos >> 8) & 0xFF);
            byte low = (byte)(signedPos & 0xFF);

            // Speed is currently unused by the hardware, send 0x00
            byte speed = 0x00;

            byte[] payload = new byte[3]
            {
        high,
        low,
        speed
            };

            can.SendFrame(canId, payload);
        }
    }
}