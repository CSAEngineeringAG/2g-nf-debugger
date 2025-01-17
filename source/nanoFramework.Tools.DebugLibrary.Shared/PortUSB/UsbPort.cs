﻿//
// Copyright (c) 2017 The nanoFramework project contributors
// Portions Copyright (c) Microsoft Corporation.  All rights reserved.
// See LICENSE file in the project root for full license information.
//

using nanoFramework.Tools.Debugger.WireProtocol;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.Usb;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.UI.Xaml;

namespace nanoFramework.Tools.Debugger.Usb
{
    public class UsbPort : PortBase, IPort
    {
        // dictionary with mapping between USB device watcher and the device ID
        private readonly Dictionary<DeviceWatcher, string> mapDeviceWatchersToDeviceSelector;

        // USB device watchers suspended flag
        private bool watchersSuspended = false;

        // USB device watchers started flag
        private bool watchersStarted = false;

        // counter of device watchers completed
        private int deviceWatchersCompletedCount = 0;

        private readonly object cancelIoLock = new object();
        private static SemaphoreSlim semaphore;

        /// <summary>
        /// Internal list with the actual nF USB devices
        /// </summary>
        readonly List<UsbDeviceInformation> UsbDevices;

        /// <summary>
        /// Creates an USB debug client
        /// </summary>
        public UsbPort(Application callerApp, bool startDeviceWatchers = true)
        {
            mapDeviceWatchersToDeviceSelector = new Dictionary<DeviceWatcher, String>();
            NanoFrameworkDevices = new ObservableCollection<NanoDeviceBase>();
            UsbDevices = new List<UsbDeviceInformation>();

            // set caller app property
            EventHandlerForUsbDevice.CallerApp = callerApp;

            // init semaphore
            semaphore = new SemaphoreSlim(1, 1);

            Task.Factory.StartNew(() =>
            {
                if (startDeviceWatchers)
                {
                    StartUsbDeviceWatchers();
                }
            });
        }

        #region Device watchers initialization

        /*////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
        Add a device watcher initialization method for each supported device that should be watched.
        That initialization method must be called from the InitializeDeviceWatchers() method above so the watcher is actually started.
        ///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////*/

        /// <summary>
        /// Initialize the device watcher for the ST Discovery4
        /// </summary>
        private void InitializeStDiscovery4DeviceWatcher()
        {
            // better use  most specific type of DeviceSelector: VID, PID and class GUID
            var stDiscovery4Selector = UsbDevice.GetDeviceSelector(STM_Discovery4.DeviceVid, STM_Discovery4.DevicePid, STM_Discovery4.DeviceInterfaceClass);

            // Create a device watcher to look for instances of this device
            var stDiscovery4Watcher = DeviceInformation.CreateWatcher(stDiscovery4Selector);

            // Allow the EventHandlerForDevice to handle device watcher events that relates or effects this device (i.e. device removal, addition, app suspension/resume)
            AddDeviceWatcher(stDiscovery4Watcher, stDiscovery4Selector);
        }

        /// <summary>
        /// Registers for Added, Removed, and Enumerated events on the provided deviceWatcher before adding it to an internal list.
        /// </summary>
        /// <param name="deviceWatcher">The device watcher to subscribe the events</param>
        /// <param name="deviceSelector">The AQS used to create the device watcher</param>
        private void AddDeviceWatcher(DeviceWatcher deviceWatcher, String deviceSelector)
        {
            deviceWatcher.Added += new TypedEventHandler<DeviceWatcher, DeviceInformation>(OnDeviceAdded);
            deviceWatcher.Removed += new TypedEventHandler<DeviceWatcher, DeviceInformationUpdate>(OnDeviceRemoved);
            deviceWatcher.EnumerationCompleted += new TypedEventHandler<DeviceWatcher, object>(OnDeviceEnumerationComplete);

            mapDeviceWatchersToDeviceSelector.Add(deviceWatcher, deviceSelector);
        }

        #endregion

        #region Device watcher management and host app status handling

        /// <summary>
        /// Initialize device watchers. Must call here the initialization methods for all devices that we want to set watch.
        /// </summary>
        private void InitializeDeviceWatchers()
        {
            // ST Discovery 4
            InitializeStDiscovery4DeviceWatcher();
        }

        public void StartUsbDeviceWatchers()
        {
            // Initialize the USB device watchers to be notified when devices are connected/removed
            StartDeviceWatchersInternal();
        }

        /// <summary>
        /// Starts all device watchers including ones that have been individually stopped.
        /// </summary>
        private void StartDeviceWatchersInternal()
        {
            // Start all device watchers
            watchersStarted = true;
            deviceWatchersCompletedCount = 0;
            IsDevicesEnumerationComplete = false;

            foreach (DeviceWatcher deviceWatcher in mapDeviceWatchersToDeviceSelector.Keys)
            {
                if ((deviceWatcher.Status != DeviceWatcherStatus.Started)
                    && (deviceWatcher.Status != DeviceWatcherStatus.EnumerationCompleted))
                {
                    deviceWatcher.Start();
                }
            }
        }

        /// <summary>
        /// Should be called on host app OnAppSuspension() event to properly handle that status.
        /// The DeviceWatchers must be stopped because device watchers will continue to raise events even if
        /// the app is in suspension, which is not desired (drains battery). The device watchers will be resumed once the app resumes too.
        /// </summary>
        public void AppSuspending()
        {
            if (watchersStarted)
            {
                watchersSuspended = true;
                StopDeviceWatchersInternal();
            }
            else
            {
                watchersSuspended = false;
            }
        }

        /// <summary>
        /// Should be called on host app OnAppResume() event to properly handle that status.
        /// See AppSuspending for why we are starting the device watchers again.
        /// </summary>
        public void AppResumed()
        {
            if (watchersSuspended)
            {
                watchersSuspended = false;
                StartDeviceWatchers();
            }
        }

        /// <summary>
        /// Stops all device watchers.
        /// </summary>
        private void StopDeviceWatchersInternal()
        {
            // Stop all device watchers
            foreach (DeviceWatcher deviceWatcher in mapDeviceWatchersToDeviceSelector.Keys)
            {
                if ((deviceWatcher.Status == DeviceWatcherStatus.Started)
                    || (deviceWatcher.Status == DeviceWatcherStatus.EnumerationCompleted))
                {
                    deviceWatcher.Stop();
                }
            }

            // Clear the list of devices so we don't have potentially disconnected devices around
            ClearDeviceEntries();

            watchersStarted = false;
        }

        #endregion

        #region Methods to manage device list add, remove, etc

        /// <summary>
        /// Creates a DeviceListEntry for a device and adds it to the list of devices
        /// </summary>
        /// <param name="deviceInformation">DeviceInformation on the device to be added to the list</param>
        /// <param name="deviceSelector">The AQS used to find this device</param>
        private async void AddDeviceToList(DeviceInformation deviceInformation, String deviceSelector)
        {
            // search the device list for a device with a matching interface ID
            var usbMatch = FindDevice(deviceInformation.Id);

            OnLogMessageAvailable(NanoDevicesEventSource.Log.CandidateDevice(deviceInformation.Id));

            // Add the device if it's new
            if (usbMatch == null)
            {
                UsbDevices.Add(new UsbDeviceInformation(deviceInformation, deviceSelector));

                // search the NanoFramework device list for a device with a matching interface ID
                var nanoFrameworkDeviceMatch = FindNanoFrameworkDevice(deviceInformation.Id);

                if (nanoFrameworkDeviceMatch == null)
                {
                    //     Create a new element for this device interface, and queue up the query of its
                    //     device information

                    var newNanoFrameworkDevice = new NanoDevice<NanoUsbDevice>();
                    //newMFDevice.DeviceInformation = new UsbDeviceInformation(deviceInformation, deviceSelector);
                    newNanoFrameworkDevice.Device.DeviceInformation = new UsbDeviceInformation(deviceInformation, deviceSelector);
                    newNanoFrameworkDevice.ConnectionPort = this;
                    newNanoFrameworkDevice.Transport = TransportType.Usb;

                    // Add the new element to the end of the list of devices
                    NanoFrameworkDevices.Add(newNanoFrameworkDevice as NanoDeviceBase);

                    // now fill in the description
                    // try opening the device to read the descriptor
                    if (await ConnectUsbDeviceAsync(newNanoFrameworkDevice.Device.DeviceInformation))
                    {
                        // the device description format is kept to maintain backwards compatibility
                        newNanoFrameworkDevice.Description = EventHandlerForUsbDevice.Current.DeviceInformation.Name + "_" + await GetDeviceDescriptor(5);

                        NanoDevicesEventSource.Log.ValidDevice(newNanoFrameworkDevice.Description + " @ " + newNanoFrameworkDevice.Device.DeviceInformation.DeviceSelector);

                        // done here, close device
                        EventHandlerForUsbDevice.Current.CloseDevice();

                    }
                    else
                    {
                        // couldn't open device, better remove it from the lists
                        NanoFrameworkDevices.Remove(newNanoFrameworkDevice as NanoDeviceBase);
                        UsbDevices.Remove(newNanoFrameworkDevice.Device.DeviceInformation);

                        NanoDevicesEventSource.Log.QuitDevice(deviceInformation.Id);

                        // can't do anything with this one, better dispose it
                        newNanoFrameworkDevice.Dispose();
                    }
                }
                else
                {
                    // this NanoFramework device is already on the list
                }
            }
        }

        private void RemoveDeviceFromList(string deviceId)
        {
            // Removes the device entry from the internal list; therefore the UI
            var deviceEntry = FindDevice(deviceId);

            NanoDevicesEventSource.Log.DeviceDeparture(deviceId);

            UsbDevices.Remove(deviceEntry);
            // get device
            var device = FindNanoFrameworkDevice(deviceId);
            // yes, remove it from collection
            NanoFrameworkDevices.Remove(device);
        }

        private void ClearDeviceEntries()
        {
            UsbDevices.Clear();
        }

        /// <summary>
        /// Searches through the existing list of devices for the first DeviceListEntry that has
        /// the specified device Id.
        /// </summary>
        /// <param name="deviceId">Id of the device that is being searched for</param>
        /// <returns>DeviceListEntry that has the provided Id; else a nullptr</returns>
        private UsbDeviceInformation FindDevice(String deviceId)
        {
            if (deviceId != null)
            {
                foreach (UsbDeviceInformation entry in UsbDevices)
                {
                    if (entry.DeviceInformation.Id == deviceId)
                    {
                        return entry;
                    }
                }
            }

            return null;
        }

        private NanoDeviceBase FindNanoFrameworkDevice(string deviceId)
        {
            if (deviceId != null)
            {

                // usbMatch.Device.DeviceInformation
                return NanoFrameworkDevices.FirstOrDefault(d => ((d as NanoDevice<NanoUsbDevice>).Device.DeviceInformation).DeviceInformation.Id == deviceId);
            }

            return null;
        }


        /// <summary>
        /// Remove the device from the device list 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="deviceInformationUpdate"></param>
        private void OnDeviceRemoved(DeviceWatcher sender, DeviceInformationUpdate deviceInformationUpdate)
        {
            RemoveDeviceFromList(deviceInformationUpdate.Id);
        }

        /// <summary>
        /// This function will add the device to the listOfDevices
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="deviceInformation"></param>
        private void OnDeviceAdded(DeviceWatcher sender, DeviceInformation deviceInformation)
        {
            AddDeviceToList(deviceInformation, mapDeviceWatchersToDeviceSelector[sender]);
        }

        #endregion

        #region Handlers and events for Device Enumeration Complete 

        private void OnDeviceEnumerationComplete(DeviceWatcher sender, object args)
        {
            // add another device watcher completed
            deviceWatchersCompletedCount++;

            if (deviceWatchersCompletedCount == mapDeviceWatchersToDeviceSelector.Count)
            {
                NanoDevicesEventSource.Log.UsbDeviceEnumerationCompleted(UsbDevices.Count);

                // all watchers have completed enumeration
                IsDevicesEnumerationComplete = true;

                // fire event that USB enumeration is complete 
                OnDeviceEnumerationCompleted();
            }
        }

        private async Task<string> GetDeviceDescriptor(int index)
        {
            try
            {
                // maximum expected length of descriptor
                uint readBufferSize = 64;

                // prepare buffer to hold the descriptor data returned from the device
                var buffer = new Windows.Storage.Streams.Buffer(readBufferSize);

                // setup packet to perform the request
                UsbSetupPacket setupPacket = new UsbSetupPacket
                {
                    RequestType = new UsbControlRequestType
                    {
                        Direction = UsbTransferDirection.In,
                        Recipient = UsbControlRecipient.SpecifiedInterface,
                        ControlTransferType = UsbControlTransferType.Vendor,
                    },
                    // request to get a descriptor
                    Request = (byte)UsbDeviceRequestType.GetDescriptor,

                    // descriptor number to be read
                    Value = (uint)index,

                    // max length of response
                    Length = readBufferSize
                };

                // send control to device
                IBuffer responseBuffer = await EventHandlerForUsbDevice.Current.Device.SendControlInTransferAsync(setupPacket, buffer);

                // read from a buffer with a data reader
                DataReader reader = DataReader.FromBuffer(responseBuffer);

                // USB data is Little Endian 
                reader.ByteOrder = ByteOrder.LittleEndian;

                // set encoding to UTF16 & Little Endian
                reader.UnicodeEncoding = UnicodeEncoding.Utf16LE;

                // read 1st byte (descriptor length)
                // not use, but still need to read it to consume the buffer
                int descriptorLenght = reader.ReadByte();
                // read 2nd byte (descriptor type)
                int descryptorType = reader.ReadByte();

                // check if this a string (string descriptor type is 0x03)
                if (descryptorType == 0x03)
                {
                    // read a string with remaining bytes available
                    // the string length is half the available bytes because it's UTF16 encoded (2 bytes for each char)
                    return reader.ReadString(reader.UnconsumedBufferLength / 2);
                }
            }
            catch (Exception)
            {
                // catch everything else, doesn't matter
            }

            // if we get here something went wrong above, so we don't have a descriptor
            return string.Empty;
        }

        protected virtual void OnDeviceEnumerationCompleted()
        {
            DeviceEnumerationCompleted?.Invoke(this, new EventArgs());
        }

        /// <summary>
        /// Event that is raised when enumeration of all watched devices is complete.
        /// </summary>

        public override event EventHandler DeviceEnumerationCompleted;

        #endregion

        public Task<bool> ConnectDeviceAsync()
        {
            return ConnectUsbDeviceAsync(null);
        }

        private Task<bool> ConnectUsbDeviceAsync(UsbDeviceInformation usbDeviceInfo)
        {
            // try to determine if we already have this device opened.
            if (EventHandlerForUsbDevice.Current != null)
            {
                // device matches
                if (EventHandlerForUsbDevice.Current.DeviceInformation == usbDeviceInfo.DeviceInformation)
                {
                    return Task.FromResult(true);
                }
            }

            // Create an EventHandlerForDevice to watch for the device we are connecting to
            EventHandlerForUsbDevice.CreateNewEventHandlerForDevice();

            return EventHandlerForUsbDevice.Current.OpenDeviceAsync(usbDeviceInfo.DeviceInformation, usbDeviceInfo.DeviceSelector);
        }

        public void DisconnectDevice(NanoDeviceBase device)
        {
            if (FindDevice(((device as NanoDevice<NanoUsbDevice>).Device.DeviceInformation).DeviceInformation.Id) != null)
            {
                EventHandlerForUsbDevice.Current.CloseDevice();
            }
        }

        #region Interface implementations

        public DateTime LastActivity { get; set; }

        public void DisconnectDevice()
        {
            EventHandlerForUsbDevice.Current.CloseDevice();
        }

        public async Task<uint> SendBufferAsync(byte[] buffer, TimeSpan waiTimeout, CancellationToken cancellationToken)
        {
            uint bytesWritten = 0;

            // device must be connected
            if (EventHandlerForUsbDevice.Current.IsDeviceConnected)
            {
                //sanity check for available OUT pipes (must have at least one!)
                if (EventHandlerForUsbDevice.Current.Device.DefaultInterface.BulkOutPipes.Count < 1)
                {
                    // FIXME
                    // throw exception?
                }

                // gets the 1st OUT stream for the device
                var stream = EventHandlerForUsbDevice.Current.Device.DefaultInterface.BulkOutPipes[0].OutputStream;

                // create a data writer to access the device OUT stream
                var writer = new DataWriter(stream);

                // write buffer
                writer.WriteBytes(buffer);

                // need to have a timeout to cancel the read task otherwise it may end up waiting forever for this to return
                var timeoutCancelatioToken = new CancellationTokenSource(waiTimeout).Token;

                // because we have an external cancellation token and the above timeout cancellation token, need to combine both
                var linkedCancelationToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancelatioToken).Token;

                Task<uint> storeAsyncTask;

                try
                {

                    // Don't start any IO if the task has been cancelled
                    lock (cancelIoLock)
                    {
                        // set this makes sure that an exception is thrown when the cancellation token is set
                        linkedCancelationToken.ThrowIfCancellationRequested();

                        // Now the buffer data is actually flushed out to the device.
                        // We should implement a cancellation Token here so we are able to stop the task operation explicitly if needed
                        // The completion function should still be called so that we can properly handle a cancelled task
                        storeAsyncTask = writer.StoreAsync().AsTask(linkedCancelationToken);
                    }

                    bytesWritten = await storeAsyncTask;

                    if (bytesWritten > 0)
                    {
                        LastActivity = DateTime.Now;
                    }
                }
                catch (TaskCanceledException)
                {
                    // this is expected to happen, don't do anything with this 
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"SendRawBufferAsync-USB-Exception occurred: {ex.Message}\r\n {ex.StackTrace}");
                    return 0;
                }
            }
            else
            {
                throw new DeviceNotConnectedException();
            }

            return bytesWritten;
        }

        public async Task<byte[]> ReadBufferAsync(uint bytesToRead, TimeSpan waiTimeout, CancellationToken cancellationToken)
        {
            // device must be connected
            if (EventHandlerForUsbDevice.Current.IsDeviceConnected)
            {
                //sanity check for available IN pipes (must have at least one!)
                if (EventHandlerForUsbDevice.Current.Device.DefaultInterface.BulkInPipes.Count < 1)
                {
                    // FIXME
                    // throw exception?
                }

                // gets the 1st IN stream for the device
                var stream = EventHandlerForUsbDevice.Current.Device.DefaultInterface.BulkInPipes[0].InputStream;

                DataReader reader = new DataReader(stream);
                uint bytesRead = 0;

                // need to have a timeout to cancel the read task otherwise it may end up waiting forever for this to return
                var timeoutCancelatioToken = new CancellationTokenSource(waiTimeout).Token;

                // because we have an external cancellation token and the above timeout cancellation token, need to combine both
                var linkedCancelationToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancelatioToken).Token;

                Task<UInt32> loadAsyncTask;

                await semaphore.WaitAsync();

                try
                {
                    //// Don't start any IO if the task has been cancelled
                    //lock (cancelIoLock)
                    //{
                    //    // set this makes sure that an exception is thrown when the cancellation token is set
                    //    linkedCancelationToken.ThrowIfCancellationRequested();

                    //    // We should implement a cancellation Token here so we are able to stop the task operation explicitly if needed
                    //    // The completion function should still be called so that we can properly handle a cancelled task
                    //    loadAsyncTask = reader.LoadAsync(bytesToRead).AsTask(linkedCancelationToken);
                    //}

                    //bytesRead = await loadAsyncTask;

                    List<byte> buffer = new List<byte>();
                    int offset = 0;

                    while (waiTimeout.TotalMilliseconds > 0)
                    {
                        uint readCount = reader.UnconsumedBufferLength;

                        // any byte to read or are we expecting any more bytes?
                        if (readCount > 0 &&
                            bytesToRead > 0)
                        {
                            await reader.LoadAsync(readCount);

                            byte[] readBuffer = new byte[bytesToRead];
                            reader.ReadBytes(readBuffer);

                            buffer.AddRange(readBuffer);

                            offset += readBuffer.Length;
                            bytesToRead -= (uint)readBuffer.Length;
                        }

                        // 
                        waiTimeout.Subtract(new TimeSpan(0, 0, 0, 0, 100));
                    }

                    return buffer.ToArray();
                }
                catch (TaskCanceledException)
                {
                    // this is expected to happen, don't do anything with this 
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"ReadBufferAsync-USB-Exception occurred: {ex.Message}\r\n {ex.StackTrace}");
                    return new byte[0];
                }
                finally
                {
                    semaphore.Release();
                }

            }
            else
            {
                throw new DeviceNotConnectedException();
            }

            // return empty byte array
            return new byte[0];
        }

        public override void StartDeviceWatchers()
        {
            throw new NotImplementedException();
        }

        public override void StopDeviceWatchers()
        {
            throw new NotImplementedException();
        }

        public override void ReScanDevices()
        {
            throw new NotImplementedException();
        }

        #endregion
    }
}
