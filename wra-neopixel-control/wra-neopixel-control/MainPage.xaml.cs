using Microsoft.Maker.Firmata;
using System;
using System.Threading;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using Windows.System;
using Windows.Devices.Enumeration;
using Windows.Devices.Spi;
using Windows.Media.Audio;
using Windows.Storage;
using Windows.Storage.Pickers;
using System.Threading.Tasks;
using Windows.Media.Render;
using Windows.Devices.Gpio;
using Windows.UI.Core;

// The Blank Page item template is documented at http://go.microsoft.com/fwlink/?LinkId=234238

namespace wra_neopixel_control
{
    struct Acceleration
    {
        public double X;
        public double Y;
        public double Z;
    };

    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        private const int NEOPIXEL_SET_COMMAND = 0x42;
        private const int NEOPIXEL_SHOW_COMMAND = 0x44;
        private const int NUMBER_OF_PIXELS = 30;

        private UwpFirmata firmata;
       
        private int _lightDelay = 100;
        private string _lightColor = "Blue";
        private int ledsValue = 0;

        //accelerometer
        private const byte ACCEL_REG_POWER_CONTROL = 0x2D;  /* Address of the Power Control register                */
        private const byte ACCEL_REG_DATA_FORMAT = 0x31;    /* Address of the Data Format register                  */
        private const byte ACCEL_REG_X = 0x32;              /* Address of the X Axis data register                  */
        private const byte ACCEL_REG_Y = 0x34;              /* Address of the Y Axis data register                  */
        private const byte ACCEL_REG_Z = 0x36;              /* Address of the Z Axis data register                  */

        private const byte SPI_CHIP_SELECT_LINE = 0;        /* Chip select line to use                              */
        private const byte ACCEL_SPI_RW_BIT = 0x80;         /* Bit used in SPI transactions to indicate read/write  */
        private const byte ACCEL_SPI_MB_BIT = 0x40;         /* Bit used to indicate multi-byte SPI transactions     */

        private SpiDevice SPIAccel;
        private Timer periodicTimer;

        //audio
        private AudioGraph graph;
        private AudioFileInputNode fileInput;
        private AudioDeviceOutputNode deviceOutput;
        private bool isAlarmOn = false;

        //buttons
        private const int BUTTON_START = 5;
        private const int BUTTON_FINISH = 6;
        private const int BUTTON_PIN1 = 23;
        private const int BUTTON_PIN2 = 18;
        private const int BUTTON_PIN3 = 25;
        private const int BUTTON_PIN4 = 24;
        private int started = 0;
        private int locked = 0;
        private int numberOfDigits = 4;
        private GpioPin buttonPin1;
        private GpioPin buttonPin2;
        private GpioPin buttonPin3;
        private GpioPin buttonPin4;
        private GpioPin buttonPinStart;
        private GpioPin buttonPinFinish;
        private GpioPinValue ledPinValue = GpioPinValue.High;
        private SolidColorBrush redBrush = new SolidColorBrush(Windows.UI.Colors.Red);
        private SolidColorBrush grayBrush = new SolidColorBrush(Windows.UI.Colors.LightGray);

        private int[] password = new int[4];
        private int[] passChecker = new int[4];
        private int pIndex = 0;
        private bool StopLights = false;


        /// <summary>
        /// This page uses advanced features of the Windows Remote Arduino library to carry out custom commands which are
        /// defined in the NeoPixel_StandardFirmata.ino sketch. This is a customization of the StandardFirmata sketch which
        /// implements the Firmata protocol. The customization defines the behaviors of the custom commands invoked by this page.
        /// 
        /// To learn more about Windows Remote Arduino, refer to the GitHub page at: https://github.com/ms-iot/remote-wiring/
        /// To learn more about advanced behaviors of WRA and how to define your own custom commands, refer to the
        /// advanced documentation here: https://github.com/ms-iot/remote-wiring/blob/develop/advanced.md
        /// </summary>
        public MainPage()
        {
            this.InitializeComponent();
            firmata = App.Firmata;


            Unloaded += MainPage_Unloaded;

            //accelerometer
            InitAccel();

            //audio
            CreateAudioGraph();

            //buttons
            InitGPIO();
        }

        private async void InitAccel()
        {
            try
            {
                var settings = new SpiConnectionSettings(SPI_CHIP_SELECT_LINE);
                settings.ClockFrequency = 5000000;                              /* 5MHz is the rated speed of the ADXL345 accelerometer                     */
                settings.Mode = SpiMode.Mode3;                                  /* The accelerometer expects an idle-high clock polarity, we use Mode3    
                                                                                 * to set the clock polarity and phase to: CPOL = 1, CPHA = 1         
                                                                                 */

                string aqs = SpiDevice.GetDeviceSelector();                     /* Get a selector string that will return all SPI controllers on the system */
                var dis = await DeviceInformation.FindAllAsync(aqs);            /* Find the SPI bus controller devices with our selector string             */
                SPIAccel = await SpiDevice.FromIdAsync(dis[0].Id, settings);    /* Create an SpiDevice with our bus controller and SPI settings             */
                if (SPIAccel == null)
                {
                    Text_Status.Text = string.Format(
                        "SPI Controller {0} is currently in use by " +
                        "another application. Please ensure that no other applications are using SPI.",
                        dis[0].Id);
                    return;
                }
            }
            catch (Exception ex)
            {
                Text_Status.Text = "SPI Initialization failed. Exception: " + ex.Message;
                return;
            }

            /* 
             * Initialize the accelerometer:
             *
             * For this device, we create 2-byte write buffers:
             * The first byte is the register address we want to write to.
             * The second byte is the contents that we want to write to the register. 
             */
            byte[] WriteBuf_DataFormat = new byte[] { ACCEL_REG_DATA_FORMAT, 0x01 };        /* 0x01 sets range to +- 4Gs                         */
            byte[] WriteBuf_PowerControl = new byte[] { ACCEL_REG_POWER_CONTROL, 0x08 };    /* 0x08 puts the accelerometer into measurement mode */

            /* Write the register settings */
            try
            {
                SPIAccel.Write(WriteBuf_DataFormat);
                SPIAccel.Write(WriteBuf_PowerControl);
            }
            /* If the write fails display the error and stop running */
            catch (Exception ex)
            {
                Text_Status.Text = "Failed to communicate with device: " + ex.Message;
                return;
            }

            /* Now that everything is initialized, create a timer so we read data every 100mS */
            periodicTimer = new Timer(this.TimerCallback, null, 0, 100);
        }

        private void MainPage_Unloaded(object sender, object args)
        {
             SPIAccel.Dispose();
        }

        private void FlipLights()
        {
            while (true)
            {
                switch (_lightColor)
                {
                    case "Red":
                        SetAllPixelsAndUpdate(255, 0, 0);
                        _lightColor = "Green";
                        break;

                    case "Green":
                        SetAllPixelsAndUpdate(0, 255, 0);
                        _lightColor = "Blue";
                        break;

                    case "Blue":
                        SetAllPixelsAndUpdate(0, 0, 255);
                        _lightColor = "Yellow";
                        break;

                    case "Yellow":
                        SetAllPixelsAndUpdate(255, 255, 0);
                        _lightColor = "Cyan";
                        break;

                    case "Cyan":
                        SetAllPixelsAndUpdate(0, 255, 255);
                        _lightColor = "Magenta";
                        break;

                    case "Magenta":
                        SetAllPixelsAndUpdate(255, 0, 255);
                        _lightColor = "Red";
                        break;
                }

                if (StopLights)
                {
                    SetAllPixelsAndUpdate(0, 0, 0);
                    break;
                }
            }
        }

        private void OnDelayValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
                _lightDelay=(int)e.NewValue*100;
        }

        /// <summary>
        /// Sets all the pixels to the given color values and calls UpdateStrip() to tell the NeoPixel library to show the set colors.
        /// </summary>
        /// <param name="red"></param>
        /// <param name="green"></param>
        /// <param name="blue"></param>
        private void SetAllPixelsAndUpdate( byte red, byte green, byte blue )
        {
            SetAllPixels( red, green, blue );
            UpdateStrip();
        }

        /// <summary>
        /// Sets all the pixels to the given color values
        /// </summary>
        /// <param name="red">The amount of red to set</param>
        /// <param name="green">The amount of green to set</param>
        /// <param name="blue">The amount of blue to set</param>
        private void SetAllPixels( byte red, byte green, byte blue )
        {
            for( byte i = 0; i < NUMBER_OF_PIXELS; ++i )
            {
                SetPixel( i, red, green, blue );
            }
        }

        /// <summary>
        /// Sets a single pixel to the given color values
        /// </summary>
        /// <param name="red">The amount of red to set</param>
        /// <param name="green">The amount of green to set</param>
        /// <param name="blue">The amount of blue to set</param>
        private void SetPixel( byte pixel, byte red, byte green, byte blue )
        {
            firmata.beginSysex( NEOPIXEL_SET_COMMAND );
            firmata.appendSysex( pixel );
            firmata.appendSysex( red );
            firmata.appendSysex( green );
            firmata.appendSysex( blue );
            firmata.endSysex();
        }

        /// <summary>
        /// Tells the NeoPixel strip to update its displayed colors.
        /// This function must be called before any colors set to pixels will be displayed.
        /// </summary>
        /// <param name="red">The amount of red to set</param>
        /// <param name="green">The amount of green to set</param>
        /// <param name="blue">The amount of blue to set</param>
        private void UpdateStrip()
        {
            firmata.beginSysex( NEOPIXEL_SHOW_COMMAND );
            firmata.endSysex();
        }

        private void TimerCallback(object state)
        {
            string xText, yText, zText;

            /* Read and format accelerometer data */
            try
            {
                Acceleration accel = ReadAccel();
                xText = String.Format("X Axis: {0:F3}G", accel.X);
                yText = String.Format("Y Axis: {0:F3}G", accel.Y);
                zText = String.Format("Z Axis: {0:F3}G", accel.Z);
                CheckIntruder(accel.Z);
            }
            catch (Exception ex)
            {
                xText = "X Axis: Error";
                yText = "Y Axis: Error";
                zText = "Z Axis: Error";
            }   
        }

        private bool CheckIntruder(double zAxis)
        {
            if (zAxis < 0.5)
            {
                if (!isAlarmOn && locked == 1)
                {
                    isAlarmOn = true;
                    StopLights = false;
                    openFile();
                    graph.Start();
                    /* UI updates must be invoked on the UI thread */
                    var task = this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        Text_Status.Text = "INTRUDER!!!";
                    });
                    FlipLights();
                    //ComposeEmail();
                    return true;
                }
            }
            return false;
        }

        private async void SendEmail()
        {
            string subject = "Intruder Alert!";
            string messageBody = "There is an intruder alert in your NeoPixelSecuritySystem";
            await Launcher.LaunchUriAsync( new Uri("mailto:mmontse.lozano@gmail.com?subject=IntruderAlertt&body="+ messageBody));
        }

        private async void ComposeEmail()
        {
            var contact = new Windows.ApplicationModel.Contacts.Contact();
            contact.Name = "Montse";
            var contactEmail = new Windows.ApplicationModel.Contacts.ContactEmail();
            contactEmail.Address = "montse_967@hotmail.com";
            contact.Emails.Add(contactEmail);

            var emailMessage = new Windows.ApplicationModel.Email.EmailMessage();
            emailMessage.Body = "Intruder alert in NeoPixel Security";
            emailMessage.Subject = "Intruder Alert!";
            var email = contact.Emails.FirstOrDefault<Windows.ApplicationModel.Contacts.ContactEmail>();
            if (email != null)
            {
                var emailRecipient = new Windows.ApplicationModel.Email.EmailRecipient(email.Address);
                emailMessage.To.Add(emailRecipient);
            }

            await Windows.ApplicationModel.Email.EmailManager.ShowComposeNewEmailAsync(emailMessage);

        }


        private Acceleration ReadAccel()
        {
            const int ACCEL_RES = 1024;         /* The ADXL345 has 10 bit resolution giving 1024 unique values                     */
            const int ACCEL_DYN_RANGE_G = 8;    /* The ADXL345 had a total dynamic range of 8G, since we're configuring it to +-4G */
            const int UNITS_PER_G = ACCEL_RES / ACCEL_DYN_RANGE_G;  /* Ratio of raw int values to G units                          */

            byte[] ReadBuf;
            byte[] RegAddrBuf;

            /* 
             * Read from the accelerometer 
             * We first write the address of the X-Axis register, then read all 3 axes into ReadBuf
             */
 
             ReadBuf = new byte[6 + 1];      /* Read buffer of size 6 bytes (2 bytes * 3 axes) + 1 byte padding */
             RegAddrBuf = new byte[1 + 6];   /* Register address buffer of size 1 byte + 6 bytes padding        */
             /* Register address we want to read from with read and multi-byte bit set                          */
             RegAddrBuf[0] = ACCEL_REG_X | ACCEL_SPI_RW_BIT | ACCEL_SPI_MB_BIT;
             SPIAccel.TransferFullDuplex(RegAddrBuf, ReadBuf);
             Array.Copy(ReadBuf, 1, ReadBuf, 0, 6);  /* Discard first dummy byte from read                      */

            /* Check the endianness of the system and flip the bytes if necessary */
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(ReadBuf, 0, 2);
                Array.Reverse(ReadBuf, 2, 2);
                Array.Reverse(ReadBuf, 4, 2);
            }

            /* In order to get the raw 16-bit data values, we need to concatenate two 8-bit bytes for each axis */
            short AccelerationRawX = BitConverter.ToInt16(ReadBuf, 0);
            short AccelerationRawY = BitConverter.ToInt16(ReadBuf, 2);
            short AccelerationRawZ = BitConverter.ToInt16(ReadBuf, 4);

            /* Convert raw values to G's */
            Acceleration accel;
            accel.X = (double)AccelerationRawX / UNITS_PER_G;
            accel.Y = (double)AccelerationRawY / UNITS_PER_G;
            accel.Z = (double)AccelerationRawZ / UNITS_PER_G;

            return accel;
        }

        private async Task<StorageFile> GetPackagedFile(string folderName, string fileName)
        {
            StorageFolder installFolder = Windows.ApplicationModel.Package.Current.InstalledLocation;

            if (folderName != null)
            {
                StorageFolder subFolder = await installFolder.GetFolderAsync(folderName);
                return await subFolder.GetFileAsync(fileName);
            }
            else
            {
                return await installFolder.GetFileAsync(fileName);
            }
        }

        private async void openFile()
        {
            // If another file is already loaded into the FileInput node
            if (fileInput != null)
            {
                // Release the file and dispose the contents of the node
                fileInput.Dispose();
            }

            FileOpenPicker filePicker = new FileOpenPicker();
            filePicker.SuggestedStartLocation = PickerLocationId.MusicLibrary;
            filePicker.FileTypeFilter.Add(".mp3");
            filePicker.FileTypeFilter.Add(".wav");
            filePicker.FileTypeFilter.Add(".wma");
            filePicker.FileTypeFilter.Add(".m4a");
            filePicker.ViewMode = PickerViewMode.Thumbnail;
            // StorageFile file = await filePicker.PickSingleFileAsync();
            StorageFile file = await GetPackagedFile(null, "alarm.mp3");
            // File can be null if cancel is hit in the file picker
            if (file == null)
            {
                return;
            }

            CreateAudioFileInputNodeResult fileInputResult = await graph.CreateFileInputNodeAsync(file);
            if (AudioFileNodeCreationStatus.Success != fileInputResult.Status)
            {
                // Cannot read input file
                return;
            }

            fileInput = fileInputResult.FileInputNode;
            fileInput.AddOutgoingConnection(deviceOutput);

            // Trim the file: set the start time to 3 seconds from the beginning
            // fileInput.EndTime can be used to trim from the end of file
            fileInput.StartTime = TimeSpan.FromSeconds(0);

            // MediaPlayer player = MediaPlayer.;
        }

        private async Task CreateAudioGraph()
        {
            // Create an AudioGraph with default settings
            AudioGraphSettings settings = new AudioGraphSettings(AudioRenderCategory.Media);
            CreateAudioGraphResult result = await AudioGraph.CreateAsync(settings);

            if (result.Status != AudioGraphCreationStatus.Success)
            {
                // Cannot create graph
                return;
            }

            graph = result.Graph;

            // Create a device output node
            CreateAudioDeviceOutputNodeResult deviceOutputNodeResult = await graph.CreateDeviceOutputNodeAsync();

            if (deviceOutputNodeResult.Status != AudioDeviceNodeCreationStatus.Success)
            {
                // Cannot create device output node
                return;
            }

            deviceOutput = deviceOutputNodeResult.DeviceOutputNode;
        }

        private void InitGPIO()
        {
            var gpio = GpioController.GetDefault();

            // Show an error if there is no GPIO controller
            if (gpio == null)
            {
                GpioStatus.Text = "There is no GPIO controller on this device.";
                return;
            }

            buttonPin1 = gpio.OpenPin(BUTTON_PIN1);
            buttonPin2 = gpio.OpenPin(BUTTON_PIN2);
            buttonPin3 = gpio.OpenPin(BUTTON_PIN3);
            buttonPin4 = gpio.OpenPin(BUTTON_PIN4);
            buttonPinStart = gpio.OpenPin(BUTTON_START);
            buttonPinFinish = gpio.OpenPin(BUTTON_FINISH);

            //PIN1
            // Check if input pull-up resistors are supported
            if (buttonPin1.IsDriveModeSupported(GpioPinDriveMode.InputPullUp))
                buttonPin1.SetDriveMode(GpioPinDriveMode.InputPullUp);
            else
                buttonPin1.SetDriveMode(GpioPinDriveMode.Input);

            // Set a debounce timeout to filter out switch bounce noise from a button press
            buttonPin1.DebounceTimeout = TimeSpan.FromMilliseconds(50);

            // Register for the ValueChanged event so our buttonPin_ValueChanged 
            // function is called when the button is pressed
            buttonPin1.ValueChanged += buttonPin1_ValueChanged;

            //PIN2
            // Check if input pull-up resistors are supported
            if (buttonPin2.IsDriveModeSupported(GpioPinDriveMode.InputPullUp))
                buttonPin2.SetDriveMode(GpioPinDriveMode.InputPullUp);
            else
                buttonPin2.SetDriveMode(GpioPinDriveMode.Input);

            // Set a debounce timeout to filter out switch bounce noise from a button press
            buttonPin2.DebounceTimeout = TimeSpan.FromMilliseconds(50);

            // Register for the ValueChanged event so our buttonPin_ValueChanged 
            // function is called when the button is pressed
            buttonPin2.ValueChanged += buttonPin2_ValueChanged;

            //PIN3
            // Check if input pull-up resistors are supported
            if (buttonPin3.IsDriveModeSupported(GpioPinDriveMode.InputPullUp))
                buttonPin3.SetDriveMode(GpioPinDriveMode.InputPullUp);
            else
                buttonPin3.SetDriveMode(GpioPinDriveMode.Input);

            // Set a debounce timeout to filter out switch bounce noise from a button press
            buttonPin3.DebounceTimeout = TimeSpan.FromMilliseconds(50);

            // Register for the ValueChanged event so our buttonPin_ValueChanged 
            // function is called when the button is pressed
            buttonPin3.ValueChanged += buttonPin3_ValueChanged;

            //PIN4
            // Check if input pull-up resistors are supported
            if (buttonPin4.IsDriveModeSupported(GpioPinDriveMode.InputPullUp))
                buttonPin4.SetDriveMode(GpioPinDriveMode.InputPullUp);
            else
                buttonPin4.SetDriveMode(GpioPinDriveMode.Input);

            // Set a debounce timeout to filter out switch bounce noise from a button press
            buttonPin4.DebounceTimeout = TimeSpan.FromMilliseconds(50);

            // Register for the ValueChanged event so our buttonPin_ValueChanged 
            // function is called when the button is pressed
            buttonPin4.ValueChanged += buttonPin4_ValueChanged;

            //PINSTART
            // Check if input pull-up resistors are supported
            if (buttonPinStart.IsDriveModeSupported(GpioPinDriveMode.InputPullUp))
                buttonPinStart.SetDriveMode(GpioPinDriveMode.InputPullUp);
            else
                buttonPinStart.SetDriveMode(GpioPinDriveMode.Input);

            // Set a debounce timeout to filter out switch bounce noise from a button press
            buttonPinStart.DebounceTimeout = TimeSpan.FromMilliseconds(50);

            // Register for the ValueChanged event so our buttonPin_ValueChanged 
            // function is called when the button is pressed
            buttonPinStart.ValueChanged += buttonPinStart_ValueChanged;

            //PINFINISH
            // Check if input pull-up resistors are supported
            if (buttonPinFinish.IsDriveModeSupported(GpioPinDriveMode.InputPullUp))
                buttonPinFinish.SetDriveMode(GpioPinDriveMode.InputPullUp);
            else
                buttonPinFinish.SetDriveMode(GpioPinDriveMode.Input);

            // Set a debounce timeout to filter out switch bounce noise from a button press
            buttonPinFinish.DebounceTimeout = TimeSpan.FromMilliseconds(50);

            // Register for the ValueChanged event so our buttonPin_ValueChanged 
            // function is called when the button is pressed
            buttonPinFinish.ValueChanged += buttonPinFinish_ValueChanged;

            //GpioStatus.Text = "GPIO pins initialized correctly.";
        }

        private void buttonPin1_ValueChanged(GpioPin sender, GpioPinValueChangedEventArgs e)
        {

            // need to invoke UI updates on the UI thread because this event
            // handler gets invoked on a separate thread.
            var task = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => {
                if (e.Edge == GpioPinEdge.FallingEdge)
                {
                    //GpioStatus.Text = "Button Pressed1";
                    if (pIndex < 4 && started == 1)
                    {
                        if (locked == 1)
                        {
                            passChecker[pIndex] = 1;
                        }
                        else
                        {
                            password[pIndex] = 1;
                        }
                        pIndex++;
                    }
                    else
                    {
                        pIndex = 0;
                        started = 0;
                    }
                }
                else
                {
                    //GpioStatus.Text = "Button Released1";
                }

                Pass1.Text = ReadArray(0);
                Pass2.Text = ReadArray(1);
            });

        }

        private void buttonPin2_ValueChanged(GpioPin sender, GpioPinValueChangedEventArgs e)
        {

            // need to invoke UI updates on the UI thread because this event
            // handler gets invoked on a separate thread.
            var task = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => {
                if (e.Edge == GpioPinEdge.FallingEdge)
                {
                    //GpioStatus.Text = "Button Pressed2";

                    if (pIndex < 4 && started == 1)
                    {
                        if (locked == 1)
                        {
                            passChecker[pIndex] = 2;
                        }
                        else
                        {
                            password[pIndex] = 2;
                        }
                        pIndex++;
                    }
                    else
                    {
                        pIndex = 0;
                        started = 0;
                    }
                }
                else
                {
                    //GpioStatus.Text = "Button Released2";
                }

                Pass1.Text = ReadArray(0);
                Pass2.Text = ReadArray(1);
            });

        }

        private void buttonPin3_ValueChanged(GpioPin sender, GpioPinValueChangedEventArgs e)
        {

            // need to invoke UI updates on the UI thread because this event
            // handler gets invoked on a separate thread.
            var task = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => {
                if (e.Edge == GpioPinEdge.FallingEdge)
                {
                    //GpioStatus.Text = "Button Pressed3";

                    if (pIndex < 4 && started == 1)
                    {
                        if (locked == 1)
                        {
                            passChecker[pIndex] = 3;
                        }
                        else
                        {
                            password[pIndex] = 3;
                        }
                        pIndex++;
                    }
                    else
                    {
                        pIndex = 0;
                        started = 0;
                    }
                }
                else
                {
                    //GpioStatus.Text = "Button Released3";
                }

                Pass1.Text = ReadArray(0);
                Pass2.Text = ReadArray(1);
            });

        }

        private void buttonPin4_ValueChanged(GpioPin sender, GpioPinValueChangedEventArgs e)
        {

            // need to invoke UI updates on the UI thread because this event
            // handler gets invoked on a separate thread.
            var task = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => {
                if (e.Edge == GpioPinEdge.FallingEdge)
                {
                    //GpioStatus.Text = "Button Pressed4";
                    if (pIndex < 4 && started == 1)
                    {
                        if (locked == 1)
                        {
                            passChecker[pIndex] = 4;
                        }
                        else
                        {
                            password[pIndex] = 4;
                        }

                        pIndex++;
                    }
                    else
                    {
                        pIndex = 0;
                        started = 0;
                    }
                }
                else
                {
                    //GpioStatus.Text = "Button Released4";
                }
                Pass1.Text = ReadArray(0);
                Pass2.Text = ReadArray(1);
            });

        }

        private void buttonPinStart_ValueChanged(GpioPin sender, GpioPinValueChangedEventArgs e)
        {
            // need to invoke UI updates on the UI thread because this event
            // handler gets invoked on a separate thread.
            var task = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => {
                if (e.Edge == GpioPinEdge.FallingEdge)
                {
                    GpioStatus.Text = "Button Start Pressed";

                    started = 1;
                    pIndex = 0;
                }
                else
                {
                    //GpioStatus.Text = "Button ReleasedStart";
                }
            });


        }

        private void buttonPinFinish_ValueChanged(GpioPin sender, GpioPinValueChangedEventArgs e)
        {

            // need to invoke UI updates on the UI thread because this event
            // handler gets invoked on a separate thread.
            var task = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => {
                if (e.Edge == GpioPinEdge.FallingEdge)
                {
                    GpioStatus.Text = "Button Finish Pressed";

                    if (pIndex == numberOfDigits)
                    {
                        started = 0;
                        if (locked == 1)
                        {
                            if (VerifyPassword())
                            {
                                locked = 0;
                                if (isAlarmOn)
                                {
                                    graph.Stop();
                                    StopLights = true;
                                    isAlarmOn = false;
                                    var taskDeleteText = this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                                    {
                                        Text_Status.Text = "";
                                    });
                                }
                            }
                        }
                        else
                        {
                            locked = 1;
                        }
                    }
                    pIndex = 0;
                }
                else
                {
                    //GpioStatus.Text = "Button ReleasedFinish";
                }

                if (locked == 0)
                {
                    Status.Text = "Unlocked";
                }
                else
                {
                    Status.Text = "Locked";
                }
            });


        }

        private bool VerifyPassword()
        {
            for (int i = 0; i < 4; i++)
            {
                if (password[i] != passChecker[i])
                {
                    return false;
                }
            }
            return true;
        }

        private string ReadArray(int which)
        {
            string number = "";

            for (int i = 0; i < 4; i++)
            {
                if (which == 0)
                {
                    number += password[i];
                }
                else
                {
                    number += passChecker[i];
                }
            }

            return number;
        }

        private void GpioStatus_SelectionChanged(object sender, RoutedEventArgs e)
        {

        }
    }
}
