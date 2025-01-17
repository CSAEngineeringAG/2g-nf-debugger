﻿//
// Copyright (c) 2017 The nanoFramework project contributors
// Portions Copyright (c) Microsoft Corporation.  All rights reserved.
// See LICENSE file in the project root for full license information.
//

using nanoFramework.Tools.Debugger.WireProtocol;
using System;
using System.Threading.Tasks;

namespace nanoFramework.Tools.Debugger
{
    public class NanoDevice<T> : NanoDeviceBase, IDisposable, INanoDevice where T : new()
    {
        public T Device { get; set; }

        public NanoDevice()
        {
            Device = new T();

            if (Device is NanoUsbDevice)
            {
                Transport = TransportType.Usb;
            }
            else if (Device is NanoSerialDevice)
            {
                Transport = TransportType.Serial;
            }
        }

        #region Disposable implementation

        public bool disposed { get; private set; }

        ~NanoDevice()
        {
            Dispose(false);
        }

        private void Dispose(bool disposing)
        {
            if (!disposed)
            {
                if (disposing)
                {
                    try
                    {
                        // release managed components
                        Disconnect();

                        DebugEngine?.Dispose();
                    }
                    catch
                    {
                        // required to catch exceptions from Engine dispose calls
                    }
                }

                disposed = true;
            }
        }

        /// <summary>
        /// Standard Dispose method for releasing resources such as the connection to the device.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);

            GC.SuppressFinalize(this);
        }

        #endregion

        /// <summary>
        /// Connect to nanoFramework device
        /// </summary>
        /// <returns>True if operation is successful</returns>
        public async Task<bool> ConnectAsync()
        {
            if (Device is NanoUsbDevice)
            {
                return await ConnectionPort.ConnectDeviceAsync();
            }
            else if (Device is NanoSerialDevice)
            {
                return await ConnectionPort.ConnectDeviceAsync();
            }

            return false;
        }

        /// <summary>
        /// Disconnect nanoFramework device
        /// </summary>
        public override void Disconnect()
        {
            ConnectionPort.DisconnectDevice();

            DeviceBase = null;
        }
    }
}
