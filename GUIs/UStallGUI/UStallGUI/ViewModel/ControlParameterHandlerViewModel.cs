using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.Command;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using UStallGUI.Helpers;
using UStallGUI.Model;

namespace UStallGUI.ViewModel
{
    public class ControlParameterHandlerViewModel : ObservableObject
    {
        public MotorValues _currentShownMotorValues;

        public MotorValues CurrentShownMotorValues
        {
            get => _currentShownMotorValues;
            set => Set(ref _currentShownMotorValues, value);
        }

        public DirectionValues _loadedDirectionValues;

        public DirectionValues LoadedDirectionValue
        {
            get => _loadedDirectionValues;
            set
            {
                Set(ref _loadedDirectionValues, value);
                DirectionSelectedIndex = _direction_selectedIndex;
            }
        }

        public RelayCommand PullDirectionValuesFromLCE_Command { get; }
        public RelayCommand ApplyDirectionValuesToLCE_Command { get; }
        public RelayCommand LoadDirectionValuesFromPC_Command { get; }
        public RelayCommand SaveDirectionValuesToPC_Command { get; }

        private int _direction_selectedIndex = 0;

        public int DirectionSelectedIndex
        {
            get => _direction_selectedIndex;
            set
            {
                _direction_selectedIndex = value;

                if (LoadedDirectionValue != null)
                {
                    switch (value)
                    {
                        case 0:
                            CurrentShownMotorValues = LoadedDirectionValue.Translation.Z_Axis;
                            break;

                        case 1:
                            CurrentShownMotorValues = LoadedDirectionValue.Translation.X_Axis;
                            break;

                        case 2:
                            CurrentShownMotorValues = LoadedDirectionValue.Translation.Y_Axis;
                            break;

                        case 3:
                            CurrentShownMotorValues = LoadedDirectionValue.Rotation.Z_Axis;
                            break;

                        case 4:
                            CurrentShownMotorValues = LoadedDirectionValue.Rotation.X_Axis;
                            break;

                        case 5:
                            CurrentShownMotorValues = LoadedDirectionValue.Rotation.Y_Axis;
                            break;
                    }
                    ;
                }
            }
        }

        private readonly UsbCANWrapper can;

        public ControlParameterHandlerViewModel(UsbCANWrapper can)
        {
            this.can = can;
            LoadedDirectionValue = DirectionValues.GetDefaultDirectionValues();
            CurrentShownMotorValues = LoadedDirectionValue.Translation.Z_Axis;
            LoadDirectionValuesFromPC_Command = new RelayCommand(LoadDirectionValuesFromPC);
            SaveDirectionValuesToPC_Command = new RelayCommand(SaveDirectionValuesToPC);
            PullDirectionValuesFromLCE_Command = new RelayCommand(PullDirectionValuesFromLCE);
            ApplyDirectionValuesToLCE_Command = new RelayCommand(ApplyDirectionValuesToLCE);
        }

        private void LoadDirectionValuesFromPC()
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "JSON files (*.json)|*.json";
            openFileDialog.Title = "Select a JSON file";

            if (openFileDialog.ShowDialog() == true)
            {
                string filePath = openFileDialog.FileName;

                DirectionValues directionValues = ConfigLoader.LoadControllerParameters(filePath);
                if (directionValues != null) LoadedDirectionValue = directionValues;
                else MessageBox.Show("That is not a vaild DirectionValue file");
            }
        }

        private void SaveDirectionValuesToPC()
        {
            SaveFileDialog saveFileDialog = new SaveFileDialog
            {
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                Title = "Save DirectionValue File",
                FileName = "directionValues.json" // Set the default filename here
            };

            // Show the dialog and check if the user clicked "Save"
            if (saveFileDialog.ShowDialog() == true)
            {
                string filePath = saveFileDialog.FileName;
                bool successful = ConfigLoader.UpdateConfigurationFile(LoadedDirectionValue, filePath);
                if (successful) MessageBox.Show("File saved successfully");
                else MessageBox.Show("Saving Failed");
            }
        }

        private const ushort dv_rx_0 = (ushort)RecieveAddressesControlUnit.PullDirectionValues_Response;
        private const ushort dv_rx_1 = (ushort)(RecieveAddressesControlUnit.PullDirectionValues_Response + 1);
        private const ushort dv_rx_2 = (ushort)(RecieveAddressesControlUnit.PullDirectionValues_Response + 2);
        private const ushort dv_rx_3 = (ushort)(RecieveAddressesControlUnit.PullDirectionValues_Response + 3);
        private const ushort dv_rx_4 = (ushort)(RecieveAddressesControlUnit.PullDirectionValues_Response + 4);
        private const ushort dv_rx_5 = (ushort)(RecieveAddressesControlUnit.PullDirectionValues_Response + 5);
        private DirectionValues recievedDirectionValues;
        private bool registeredCallbackPullData = false;

        private async void PullDirectionValuesFromLCE()
        {
            if (can != null)
            {
                recievedDirectionValues = DirectionValues.GetInvalidDirectionValues();
                async void OnSuccessfulResponse(ushort id, byte[] data)
                {
                    if (data.Length != 0)
                    {
                        float[] floatValues = LCECommunicationHelper.ConvertBytesToMotorValues(data);
                        Console.WriteLine("Received Direction Values from LCE: " + string.Join(", ", floatValues));
                        switch (id)
                        {
                            case dv_rx_0:
                                recievedDirectionValues.UpdateSingleAxis(Direction.Translation, Axis.Z_Axis, floatValues);
                                break;

                            case dv_rx_1:
                                recievedDirectionValues.UpdateSingleAxis(Direction.Translation, Axis.X_Axis, floatValues);
                                break;

                            case dv_rx_2:
                                recievedDirectionValues.UpdateSingleAxis(Direction.Translation, Axis.Y_Axis, floatValues);
                                break;

                            case dv_rx_3:
                                recievedDirectionValues.UpdateSingleAxis(Direction.Rotation, Axis.Z_Axis, floatValues);
                                break;

                            case dv_rx_4:
                                recievedDirectionValues.UpdateSingleAxis(Direction.Rotation, Axis.X_Axis, floatValues);
                                break;

                            case dv_rx_5:
                                recievedDirectionValues.UpdateSingleAxis(Direction.Rotation, Axis.Y_Axis, floatValues);
                                break;
                        }
                    }
                    else
                    {
                        MainWindowViewModel.Instance.LogToControlBox("Requesting Direction Values from LCE failed - Invalid Data");
                        return;
                    }
                }

                if (!registeredCallbackPullData)
                {
                    can.RegisterCallback(dv_rx_0, OnSuccessfulResponse);
                    can.RegisterCallback(dv_rx_1, OnSuccessfulResponse);
                    can.RegisterCallback(dv_rx_2, OnSuccessfulResponse);
                    can.RegisterCallback(dv_rx_3, OnSuccessfulResponse);
                    can.RegisterCallback(dv_rx_4, OnSuccessfulResponse);
                    can.RegisterCallback(dv_rx_5, OnSuccessfulResponse);
                    registeredCallbackPullData = true;
                }

                can.SendFrame((ushort)SendAddressesControlUnit.PullDirectionValues, []);
                await Task.Delay(500);

                if (recievedDirectionValues.HasInvalidData())
                    MainWindowViewModel.Instance.LogToControlBox("Requesting Direction Values from LCE failed - Invalid Data");
                else
                {
                    LoadedDirectionValue = recievedDirectionValues;
                    MainWindowViewModel.Instance.LogToControlBox("Requesting Direction Values from LCE successful");
                }
            }
            else MainWindowViewModel.Instance.LogToControlBox("Requesting Direction Values from LCE failed - Serial Port is not open");
        }

        private const ushort dv_tx_0 = (ushort)SendAddressesControlUnit.ApplyDirectionValues;
        private const ushort dv_tx_1 = (ushort)(SendAddressesControlUnit.ApplyDirectionValues + 1);
        private const ushort dv_tx_2 = (ushort)(SendAddressesControlUnit.ApplyDirectionValues + 2);
        private const ushort dv_tx_3 = (ushort)(SendAddressesControlUnit.ApplyDirectionValues + 3);
        private const ushort dv_tx_4 = (ushort)(SendAddressesControlUnit.ApplyDirectionValues + 4);
        private const ushort dv_tx_5 = (ushort)(SendAddressesControlUnit.ApplyDirectionValues + 5);
        private bool registeredCallbackApplyData = false;
        private bool gotResponse = false;

        private async void ApplyDirectionValuesToLCE()
        {
            if (LoadedDirectionValue == null)
            {
                MainWindowViewModel.Instance.LogToControlBox("Writing Direction Values to LCE failed - No Direction Values loaded");
                return;
            }
            if (can != null)
            {
                gotResponse = false;
                ushort[] addresses = { dv_tx_0, dv_tx_1, dv_tx_2, dv_tx_3, dv_tx_4, dv_tx_5 };
                byte[][] payload = new byte[6][];
                payload[0] = (LCECommunicationHelper.ConvertMotorValuesToBytes(LoadedDirectionValue.Translation.Z_Axis.GetAsArray()));
                payload[1] = (LCECommunicationHelper.ConvertMotorValuesToBytes(LoadedDirectionValue.Translation.X_Axis.GetAsArray()));
                payload[2] = (LCECommunicationHelper.ConvertMotorValuesToBytes(LoadedDirectionValue.Translation.Y_Axis.GetAsArray()));
                payload[3] = (LCECommunicationHelper.ConvertMotorValuesToBytes(LoadedDirectionValue.Rotation.Z_Axis.GetAsArray()));
                payload[4] = (LCECommunicationHelper.ConvertMotorValuesToBytes(LoadedDirectionValue.Rotation.X_Axis.GetAsArray()));
                payload[5] = (LCECommunicationHelper.ConvertMotorValuesToBytes(LoadedDirectionValue.Rotation.Y_Axis.GetAsArray()));

                async void OnSuccessfulResponse(ushort id, byte[] data)
                {
                    gotResponse = true;
                    MainWindowViewModel.Instance.LogToControlBox("Writing Direction Values to LCE successful");
                }
                if (!registeredCallbackApplyData)
                {
                    can.RegisterCallback((ushort)RecieveAddressesControlUnit.ApplyDirectionValues_Response, OnSuccessfulResponse);
                    registeredCallbackApplyData = true;
                }
                for (int i = 0; i < 6; i++)
                {
                    can.SendFrame(addresses[i], payload[i]);
                    await Task.Delay(100);
                }
                await Task.Delay(500);
                if (!gotResponse) MainWindowViewModel.Instance.LogToControlBox("Writing Direction Values to LCE failed - Timeout Error ");
            }
            else MainWindowViewModel.Instance.LogToControlBox("Writing Direction Values to LCE failed - Serial Port is not open");
        }
    }
}