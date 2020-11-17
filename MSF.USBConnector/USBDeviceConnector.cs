﻿// <copyright file="USBDeviceConnector.cs" company="Mechanical Squid Factory">
// Copyright © Mechanical Squid Factory All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Caliburn.Micro;
using Device.Net;
using Hid.Net.Windows;
using MSF.USBConnector.Events;
using MSF.USBMessages;

namespace MSF.USBConnector
{
  /// <summary>Class for usb device connection.</summary>
  public class USBDeviceConnector : IUSBDeviceConnector
  {
    private const int PollMilliseconds = 3000;

    private readonly IEventAggregator eventAggregator;

    /// <summary>
    /// Initializes a new instance of the <see cref="USBDeviceConnector"/> class.
    /// </summary>
    /// <param name="aggregator">Event Aggregator wired in from caliburn micro.</param>
    public USBDeviceConnector(IEventAggregator aggregator)
    {
      this.eventAggregator = aggregator;
      WindowsHidDeviceFactory.Register(this.DeviceLogger, this.DeviceTracer);
    }

    /// <inheritdoc/>
    public DeviceListener DeviceListener { get; private set; }

    /// <inheritdoc/>
    public ILogger DeviceLogger { get; } = new DebugLogger();

    /// <inheritdoc/>
    public ITracer DeviceTracer { get; } = new DebugTracer();

        /// <inheritdoc/>
    public virtual IDevice SelectedUSBDevice { get; set; }

    /// <inheritdoc/>
    public List<IDevice> USBDeviceList { get; private set; } = new List<IDevice>();

    /// <inheritdoc/>
    public List<FilterDeviceDefinition> DeviceFilters { get; private set; } = new List<FilterDeviceDefinition>();

    /// <summary>Sets up Device Listener.</summary>
    public void SetupDeviceListener()
    {
      this.DeviceListener?.Dispose();
      this.DeviceListener = new DeviceListener(this.DeviceFilters, PollMilliseconds) { Logger = this.DeviceLogger };
      this.DeviceListener.DeviceDisconnected += this.DeviceDisconnectEvent;
      this.DeviceListener.DeviceInitialized += this.DeviceConnectedEvent;
      this.DeviceListener.Start();
    }

    /// <inheritdoc/>
    public void AddHidDeviceToFilterList(uint queryVendorID, uint? queryProductID)
    {
      this.DeviceFilters.Add(new FilterDeviceDefinition { DeviceType = DeviceType.Hid, VendorId = queryVendorID, ProductId = queryProductID });
    }

    /// <inheritdoc/>
    public async Task UpdateUSBHIDDeviceList()
    {
      try
      {
        this.USBDeviceList = await DeviceManager.Current.GetDevicesAsync(this.DeviceFilters).ConfigureAwait(false);
      }
      catch (Exception)
      {
        throw new Exception($"Failed to retrieve filtered list of USB HID Devices that match the filtered list.");
      }
    }

    /// <inheritdoc/>
    public void FindFirstMatchingUSBHIDDevice()
    {
      this.SelectedUSBDevice = this.USBDeviceList.FirstOrDefault();
    }

    /// <inheritdoc/>
    public async Task OpenUSBDevice()
    {
      try
      {
        if (this.SelectedUSBDevice != null)
        {
          await this.SelectedUSBDevice.InitializeAsync().ConfigureAwait(false);
        }
      }
      catch (ArgumentNullException)
      {
        throw new Exception("USB Device to open was null. Select and assign a device to class.");
      }
    }

    /// <inheritdoc/>
    public void CloseUSBDevice()
    {
      try
      {
        if (this.SelectedUSBDevice != null)
        {
          this.SelectedUSBDevice.Close();
        }
      }
      catch (ArgumentNullException)
      {
        throw new Exception("Tried to close a USB Device Stream, but the stream was null. Can only close a stream that is opened.");
      }
    }

    /// <inheritdoc/>
    public virtual void ParsePayload(ReadResult receivedData)
    {
      ReceivableSimpleUSBMessage message = ReceivableSimpleUSBMessage.FromBytes<ReceivableSimpleUSBMessage>(receivedData);
      this.ReceivedUSBMessageHandler(message);
    }

    /// <inheritdoc/>
    public virtual void ReceivedUSBMessageHandler(IReceivableUSBMessage message)
    {
      this.eventAggregator.PublishOnBackgroundThreadAsync(message);
    }

    /// <inheritdoc/>
    public async Task WriteAndReadUSBDevice(byte[] writeData)
    {
      ReadResult readBytes = await this.SelectedUSBDevice.WriteAndReadAsync(writeData).ConfigureAwait(false);
      this.ParsePayload(readBytes);
    }

    /// <inheritdoc/>
    public async Task ReadUSBDevice()
    {
      if ((this.SelectedUSBDevice != null) && this.SelectedUSBDevice.IsInitialized)
      {
        ReadResult readBytes = await this.SelectedUSBDevice.ReadAsync().ConfigureAwait(false);
        this.ParsePayload(readBytes);
      }
      else
      {
        throw new Exception("Selected device is null or not initialized.");
      }
    }

    /// <inheritdoc/>
    public async Task WriteUSBDevice(byte[] sendData)
    {
      if (sendData != null)
      {
        if (this.SelectedUSBDevice != null)
        {
          if (sendData.Length <= this.SelectedUSBDevice.ConnectedDeviceDefinition?.WriteBufferSize)
          {
            byte[] data = new byte[(int)this.SelectedUSBDevice.ConnectedDeviceDefinition.WriteBufferSize];
            Buffer.BlockCopy(sendData, 0, data, 0, sendData.Length);
            try
            {
              await this.SelectedUSBDevice.WriteAsync(data).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
              throw new Exception("Error trying to write to device.", ex);
            }
          }
          else
          {
            throw new ArgumentOutOfRangeException(nameof(sendData), "Cannot write data larger than buffer");
          }
        }
        else
        {
          throw new ArgumentNullException(nameof(this.SelectedUSBDevice), "Trying to write but a device is not selected.");
        }
      }
      else
      {
        {
          throw new ArgumentNullException(nameof(sendData), "Data to write to device cannot be null");
        }
      }
    }

    /// <inheritdoc/>
    public async Task ContinousRead()
    {
      while (true)
      {
        if (this.SelectedUSBDevice != null)
        {
          if (this.SelectedUSBDevice.IsInitialized)
          {
            try
            {
              await this.ReadUSBDevice().ConfigureAwait(false);
            }
            catch (System.IO.IOException ex) when (ex.InnerException?.Message == "The device is not connected.")
            {
              this.SelectDevice(null);
            }
            catch (System.Exception ex) when (ex.Message == "The device has not been initialized.")
            {
              System.Diagnostics.Debug.WriteLine("Device isn't initialized for reading yet");
            }
            catch (Exception ex)
            {
              throw new Exception("Exception occured when trying to continously read a usb device.", ex);
            }
          }
        }
      }
    }

    /// <inheritdoc/>
    public async Task SendUSBMessage(ISendableUSBMessage messageToSend)
    {
      if (messageToSend != null)
      {
        var bytes = messageToSend.ToBytes();
        await this.WriteUSBDevice(messageToSend.ToBytes()).ConfigureAwait(true);
      }
      else
      {
        throw new ArgumentException("Message to send cannot be null.", nameof(messageToSend));
      }
    }

    /// <inheritdoc/>
    public void SelectDevice(IDevice selectDevice)
    {
      this.SelectedUSBDevice = selectDevice;
    }

    /// <summary>
    /// Checks if the device list contains a device (Compares DeviceIDs).
    /// </summary>
    /// <param name="device">Device to check for.</param>
    /// <returns>True if exists in list.</returns>
    protected internal bool DoesDeviceListContainDevice(IDevice device)
    {
      foreach (IDevice checkDevice in this.USBDeviceList)
      {
        if (checkDevice.DeviceId == device?.DeviceId)
        {
          return true;
        }
      }

      return false;
    }

    /// <summary>
    /// Event Called when filtered device connects.
    /// </summary>
    /// <param name="sender">Event Sender.</param>
    /// <param name="e">Event Arguments.</param>
    private void DeviceConnectedEvent(object sender, DeviceEventArgs e)
    {
      this.eventAggregator.PublishOnUIThreadAsync(new DeviceConnectedEvent(sender, e));
    }

    /// <summary>
    /// Event Called when filtered device connects.
    /// </summary>
    /// <param name="sender">Event Sender.</param>
    /// <param name="e">Event Arguments.</param>
    private void DeviceDisconnectEvent(object sender, DeviceEventArgs e)
    {
      this.eventAggregator.PublishOnUIThreadAsync(new DeviceDisconnectedEvent(sender, e));
    }
  }
}
