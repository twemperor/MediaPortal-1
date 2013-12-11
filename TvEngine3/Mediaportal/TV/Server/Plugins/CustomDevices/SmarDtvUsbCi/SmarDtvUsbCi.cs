﻿#region Copyright (C) 2005-2011 Team MediaPortal

// Copyright (C) 2005-2011 Team MediaPortal
// http://www.team-mediaportal.com
// 
// MediaPortal is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 2 of the License, or
// (at your option) any later version.
// 
// MediaPortal is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with MediaPortal. If not, see <http://www.gnu.org/licenses/>.

#endregion

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.InteropServices;
using DirectShowLib;
using Mediaportal.TV.Server.Plugins.Base.Interfaces;
using Mediaportal.TV.Server.TVControl.Interfaces.Services;
using Mediaportal.TV.Server.TVDatabase.Entities;
using Mediaportal.TV.Server.TVDatabase.Entities.Enums;
using Mediaportal.TV.Server.TVDatabase.TVBusinessLayer;
using Mediaportal.TV.Server.TVLibrary.Interfaces;
using Mediaportal.TV.Server.TVLibrary.Interfaces.Interfaces;
using Mediaportal.TV.Server.TVLibrary.Interfaces.Logging;
using Mediaportal.TV.Server.TVLibrary.Interfaces.TunerExtension;

namespace Mediaportal.TV.Server.Plugins.TunerExtension.SmarDtvUsbCi
{
  /// <summary>
  /// A class for handling conditional access with the Hauppauge WinTV-CI and TerraTec USB CI. Both devices are
  /// based on an OEM design by SmarDTV.
  /// </summary>
  public class SmarDtvUsbCi : BaseCustomDevice, IDirectShowAddOnDevice, IConditionalAccessProvider, ICiMenuActions, ITvServerPlugin
  {
    #region enums

    private enum SmarDtvCiState : int
    {
      Unplugged = 0,
      Empty,
      CamPresent
    }

    #endregion

    #region callback definitions

    /// <summary>
    /// Called by the driver when the CI slot state changes.
    /// </summary>
    /// <param name="ciFilter">The CI filter.</param>
    /// <param name="state">The new state of the slot.</param>
    /// <returns>an HRESULT indicating whether the state change was successfully handled</returns>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int OnSmarDtvUsbCiState(IBaseFilter ciFilter, SmarDtvCiState state);

    /// <summary>
    /// Called by the driver when application information is received from a CAM.
    /// </summary>
    /// <param name="ciFilter">The CI filter.</param>
    /// <param name="info">A buffer containing the application information.</param>
    /// <returns>an HRESULT indicating whether the application information was successfully processed</returns>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int OnSmarDtvUsbCiApplicationInfo(IBaseFilter ciFilter, IntPtr info);

    /// <summary>
    /// Called by the driver when a CAM wants to close an MMI session.
    /// </summary>
    /// <param name="ciFilter">The CI filter.</param>
    /// <returns>an HRESULT indicating whether the MMI session was successfully closed</returns>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int OnSmarDtvUsbCiCloseMmi(IBaseFilter ciFilter);

    /// <summary>
    /// Called by the driver when an application protocol data unit is received from a CAM.
    /// </summary>
    /// <param name="ciFilter">The CI filter.</param>
    /// <param name="apduLength">The length of the APDU buffer in bytes.</param>
    /// <param name="apdu">A buffer containing the APDU.</param>
    /// <returns>an HRESULT indicating whether the APDU was successfully processed</returns>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int OnSmarDtvUsbCiApdu(IBaseFilter ciFilter, int apduLength, IntPtr apdu);

    #endregion

    #region structs

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct SmarDtvUsbCiCallbacks
    {
      public IntPtr Context;  // Optional context that the interface will pass back as a parameter when the delegates are executed.
      public OnSmarDtvUsbCiState OnCiState;
      public OnSmarDtvUsbCiApplicationInfo OnApplicationInfo;
      public OnSmarDtvUsbCiCloseMmi OnCloseMmi;
      public OnSmarDtvUsbCiApdu OnApdu;
    }

    #pragma warning disable 0649, 0169
    // These structs are used - they are marshaled from the COM interface.

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
    private struct VersionInfo
    {
      [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 12)]
      public string PluginVersion;
      [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 12)]
      public string BdaVersion;
      [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 12)]
      public string UsbVersion;
      [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 8)]
      public string FirmwareVersion;
      [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 8)]
      public string FpgaVersion;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
    private struct ApplicationInfo
    {
      public MmiApplicationType ApplicationType;
      private byte Padding;
      public ushort Manufacturer;
      public ushort Code;
      [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 352)]
      public string MenuTitle;
    }

    #pragma warning restore 0649, 0169

    #endregion

    #region constants

    //private static readonly int APPLICATION_INFO_SIZE = Marshal.SizeOf(typeof(ApplicationInfo));    // 358
    private static readonly int VERSION_INFO_SIZE = Marshal.SizeOf(typeof(VersionInfo));            // 52
    private static readonly int CI_CALLBACKS_SIZE = Marshal.SizeOf(typeof(SmarDtvUsbCiCallbacks));  // 20

    #endregion

    #region variables

    // We use this hash to keep track of the devices that are in use. Each CI device can only be used with one
    // tuner at any given time, and only one CI device of each brand may be connected to a single system.
    private static HashSet<string> _devicesInUse = new HashSet<string>();

    private bool _isSmarDtvUsbCi = false;
    #pragma warning disable 0414
    private bool _isCamPresent = false;
    #pragma warning restore 0414
    private bool _isCamReady = false;
    private SmarDtvCiState _ciState = SmarDtvCiState.Empty;

    private IBaseFilter _ciFilter = null;
    private Type _ciType = null;
    private DsDevice _ciDevice = null;
    private IFilterGraph2 _graph = null;

    // Callbacks
    private ICiMenuCallbacks _ciMenuCallbacks;
    private SmarDtvUsbCiCallbacks _ciCallbacks;
    private IntPtr _ciCallbackBuffer = IntPtr.Zero;

    #endregion

    #region callback handlers

    /// <summary>
    /// Called by the driver when the CI slot state changes.
    /// </summary>
    /// <param name="ciFilter">The CI filter.</param>
    /// <param name="state">The new state of the slot.</param>
    /// <returns>an HRESULT indicating whether the state change was successfully handled</returns>
    private int OnCiState(IBaseFilter ciFilter, SmarDtvCiState state)
    {
      this.LogDebug("SmarDTV USB CI: CI state change callback");
      this.LogDebug("  old state  = {0}", _ciState);
      this.LogDebug("  new state  = {0}", state);
      _ciState = state;

      _isCamReady = false;
      if (state == SmarDtvCiState.Empty)
      {
        _isCamPresent = false;
      }
      else if (state == SmarDtvCiState.CamPresent)
      {
        _isCamPresent = true;
      }
      return 0;
    }

    /// <summary>
    /// Called by the driver when application information is received from a CAM.
    /// </summary>
    /// <param name="ciFilter">The CI filter.</param>
    /// <param name="info">A buffer containing the application information.</param>
    /// <returns>an HRESULT indicating whether the application information was successfully processed</returns>
    private int OnApplicationInfo(IBaseFilter ciFilter, IntPtr info)
    {
      this.LogInfo("SmarDTV USB CI: application information callback");
      //DVB_MMI.DumpBinary(info, 0, APPLICATION_INFO_SIZE);
      ApplicationInfo appInfo = (ApplicationInfo)Marshal.PtrToStructure(info, typeof(ApplicationInfo));
      this.LogDebug("  type         = {0}", appInfo.ApplicationType);
      // Note: current drivers seem to have a bug that causes only the first byte in the manufacturer and code
      // fields to be available.
      this.LogDebug("  manufacturer = 0x{0:x}", appInfo.Manufacturer);
      this.LogDebug("  code         = 0x{0:x}", appInfo.Code);
      this.LogDebug("  menu title   = {0}", appInfo.MenuTitle);

      // Receiving application information indicates that the CAM is ready for interaction.
      _isCamPresent = true;
      _isCamReady = true;
      return 0;
    }

    /// <summary>
    /// Called by the driver when a CAM wants to close an MMI session.
    /// </summary>
    /// <param name="ciFilter">The CI filter.</param>
    /// <returns>an HRESULT indicating whether the MMI session was successfully closed</returns>
    private int OnCloseMmi(IBaseFilter ciFilter)
    {
      this.LogDebug("SmarDTV USB CI: close MMI callback");
      if (_ciMenuCallbacks != null)
      {
        try
        {
          _ciMenuCallbacks.OnCiCloseDisplay(0);
        }
        catch (Exception ex)
        {
          this.LogError(ex, "SmarDTV USB CI: close MMI callback exception\r\n{0}");
        }
      }
      else
      {
        this.LogDebug("SmarDTV USB CI: menu callbacks are not set");
      }
      return 0;
    }

    /// <summary>
    /// Called by the driver when an application protocol data unit is received from a CAM.
    /// </summary>
    /// <param name="ciFilter">The CI filter.</param>
    /// <param name="apduLength">The length of the APDU buffer in bytes.</param>
    /// <param name="apdu">A buffer containing the APDU.</param>
    /// <returns>an HRESULT indicating whether the APDU was successfully processed</returns>
    private int OnApdu(IBaseFilter ciFilter, int apduLength, IntPtr apdu)
    {
      this.LogInfo("SmarDTV USB CI: APDU callback");

      //DVB_MMI.DumpBinary(apdu, 0, apduLength);
      byte[] apduBytes = new byte[apduLength];
      Marshal.Copy(apdu, apduBytes, 0, apduLength);
      DvbMmiHandler.HandleMmiData(apduBytes, ref _ciMenuCallbacks);
      return 0;
    }

    #endregion

    #region ICustomDevice members

    /// <summary>
    /// Attempt to initialise the device-specific interfaces supported by the class. If initialisation fails,
    /// the ICustomDevice instance should be disposed immediately.
    /// </summary>
    /// <param name="tunerExternalIdentifier">The external identifier for the tuner.</param>
    /// <param name="tunerType">The tuner type (eg. DVB-S, DVB-T... etc.).</param>
    /// <param name="context">Context required to initialise the interface.</param>
    /// <returns><c>true</c> if the interfaces are successfully initialised, otherwise <c>false</c></returns>
    public override bool Initialise(string tunerExternalIdentifier, CardType tunerType, object context)
    {
      this.LogDebug("SmarDTV USB CI: initialising device");

      if (string.IsNullOrEmpty(tunerExternalIdentifier))
      {
        this.LogDebug("SmarDTV USB CI: tuner external identifier is not set");
        return false;
      }
      if (_isSmarDtvUsbCi)
      {
        this.LogDebug("SmarDTV USB CI: device is already initialised");
        return true;
      }

      // A machine may only have one instance of each OEM product installed - this is a driver limitation. It
      // is unknown whether a single machine may have multiple instances by connecting instances of different
      // products (we don't explicitly prevent this). The TV Server plugin allows each OEM CI product to be
      // linked to a single tuner. Here we need to know whether this tuner (ie. the one referred to by the
      // external identifier) is currently linked to any of the products.
      Card tuner = CardManagement.GetCardByDevicePath(tunerExternalIdentifier, CardIncludeRelationEnum.None);
      if (tuner == null)
      {
        this.LogDebug("SmarDTV USB CI: tuner device ID not found in database");
        return false;
      }

      string tunerIdAsString = tuner.IdCard.ToString(CultureInfo.InvariantCulture);
      ReadOnlyCollection<SmarDtvUsbCiProduct> productList = SmarDtvUsbCiProducts.GetProductList();
      foreach (SmarDtvUsbCiProduct p in productList)
      {
        if (SettingsManagement.GetValue(p.DbSettingName, "-1").Equals(tunerIdAsString))
        {
          continue;
        }

        this.LogDebug("SmarDTV USB CI: this is the preferred device for CI product \"{0}\"", p.ProductName);
        lock (_devicesInUse)
        {
          // Check if the CI device is actually installed in this system.
          DsDevice[] captureDevices = DsDevice.GetDevicesOfCat(FilterCategory.AMKSCapture);
          try
          {
            foreach (DsDevice captureDevice in captureDevices)
            {
              if (captureDevice.Name != null && captureDevice.Name.Equals(p.BdaDeviceName))
              {
                this.LogDebug("SmarDTV USB CI: found corresponding CI device");
                if (_devicesInUse.Contains(captureDevice.DevicePath))
                {
                  this.LogDebug("SmarDTV USB CI: the CI device is already in use");
                  continue;
                }
                this.LogDebug("SmarDTV USB CI: supported device detected");
                _isSmarDtvUsbCi = true;
                _ciType = p.ComInterface;
                _ciDevice = captureDevice;
                _devicesInUse.Add(_ciDevice.DevicePath);
                return true;
              }
            }
          }
          finally
          {
            foreach (DsDevice d in captureDevices)
            {
              if (d != _ciDevice)
              {
                d.Dispose();
              }
            }
          }
        }
        this.LogDebug("SmarDTV USB CI: CI device not found");
      }

      this.LogDebug("SmarDTV USB CI: device not linked to any CI products or otherwise not supported");
      _isSmarDtvUsbCi = false;
      return false;
    }

    #endregion

    #region IDirectShowAddOnDevice member

    /// <summary>
    /// Insert and connect additional filter(s) into the graph.
    /// </summary>
    /// <param name="graph">The tuner filter graph.</param>
    /// <param name="lastFilter">The source filter (usually either a capture/receiver or
    ///   multiplexer filter) to connect the [first] additional filter to.</param>
    /// <returns><c>true</c> if one or more additional filters were successfully added to the graph, otherwise <c>false</c></returns>
    public bool AddToGraph(IFilterGraph2 graph, ref IBaseFilter lastFilter)
    {
      this.LogDebug("SmarDTV USB CI: add to graph");

      if (!_isSmarDtvUsbCi || _ciDevice == null)
      {
        this.LogDebug("SmarDTV USB CI: device not initialised or interface not supported");
        return false;
      }
      if (graph == null)
      {
        this.LogDebug("SmarDTV USB CI: graph is null");
        return false;
      }
      if (lastFilter == null)
      {
        this.LogDebug("SmarDTV USB CI: last filter is null");
        return false;
      }
      if (_ciFilter != null)
      {
        this.LogDebug("SmarDTV USB CI: device filter already in graph");
        return true;
      }

      bool success = false;
      _graph = graph;
      try
      {
        // Add the CI filter to the graph.
        int hr = _graph.AddSourceFilterForMoniker(_ciDevice.Mon, null, _ciDevice.Name, out _ciFilter);
        if (hr != (int)HResult.Severity.Success)
        {
          this.LogDebug("SmarDTV USB CI: failed to add the filter to the graph, hr = 0x{0:x} ({1})", hr, HResult.GetDXErrorString(hr));
          return false;
        }

        // Connect the filter into the graph.
        IPin tmpOutputPin = DsFindPin.ByDirection(lastFilter, PinDirection.Output, 0);
        IPin tmpInputPin = DsFindPin.ByDirection(_ciFilter, PinDirection.Input, 0);
        try
        {
          if (tmpInputPin == null || tmpOutputPin == null)
          {
            this.LogDebug("SmarDTV USB CI: failed to locate required pins");
            return false;
          }
          hr = _graph.ConnectDirect(tmpOutputPin, tmpInputPin, null);
        }
        finally
        {
          Release.ComObject("SmarDTV upstream filter output pin", ref tmpOutputPin);
          Release.ComObject("SmarDTV CI filter input pin", ref tmpInputPin);
        }
        if (hr != (int)HResult.Severity.Success)
        {
          this.LogDebug("SmarDTV USB CI: failed to connect the CI filter into the graph, hr = 0x{0:x} ({1})", hr, HResult.GetDXErrorString(hr));
          return false;
        }

        success = true;
      }
      finally
      {
        if (!success)
        {
          // We're not too worried about cleanup here as Dispose() will be called shortly. We just want to make
          // sure that we don't leave the CI filter in the graph if it can't be used.
          _graph.RemoveFilter(_ciFilter);
          Release.ComObject("SmarDTV CI filter", ref _ciFilter);
        }
      }

      // Success!
      this.LogDebug("SmarDTV USB CI: result = success");
      lastFilter = _ciFilter;
      return true;
    }

    #endregion

    #region ITvServerPlugin members

    /// <summary>
    /// A human-readable name for the device. This could be a manufacturer or reseller name, or even a model
    /// name/number.
    /// </summary>
    public override string Name
    {
      get
      {
        return "SmarDTV USB CI";
      }
    }

    /// <summary>
    /// The version of this TV Server plugin.
    /// </summary>
    public string Version
    {
      get
      {
        return "1.0.0.0";
      }
    }

    /// <summary>
    /// The author of this TV Server plugin.
    /// </summary>
    public string Author
    {
      get
      {
        return "mm1352000";
      }
    }

    /// <summary>
    /// Determine whether this TV Server plugin should only run on the master server, or if it can also
    /// run on slave servers.
    /// </summary>
    /// <remarks>
    /// This property is obsolete. Master-slave configurations are not supported.
    /// </remarks>
    public bool MasterOnly
    {
      get
      {
        return true;
      }
    }

    /// <summary>
    /// Get an instance of the configuration section for use in TV Server configuration (SetupTv).
    /// </summary>
    public Mediaportal.TV.Server.SetupControls.SectionSettings Setup
    {
      get { return new SmarDtvUsbCiConfig(); }
    }

    /// <summary>
    /// Start this TV Server plugin.
    /// </summary>
    public void Start(IInternalControllerService controllerService)
    {
    }

    /// <summary>
    /// Stop this TV Server plugin.
    /// </summary>
    public void Stop()
    {
    }

    #endregion

    #region IConditionalAccessProvider members

    /// <summary>
    /// Open the conditional access interface. For the interface to be opened successfully it is expected
    /// that any necessary hardware (such as a CI slot) is connected.
    /// </summary>
    /// <returns><c>true</c> if the interface is successfully opened, otherwise <c>false</c></returns>
    public bool OpenInterface()
    {
      this.LogDebug("SmarDTV USB CI: open conditional access interface");

      if (!_isSmarDtvUsbCi)
      {
        this.LogDebug("SmarDTV USB CI: device not initialised or interface not supported");
        return false;
      }
      if (_ciFilter == null)
      {
        this.LogDebug("SmarDTV USB CI: device filter not added to the BDA filter graph");
        return false;
      }
      if (_ciCallbackBuffer != IntPtr.Zero)
      {
        this.LogDebug("SmarDTV USB CI: interface is already open");
        return false;
      }

      // Set up callbacks and open the interface.
      _ciCallbacks = new SmarDtvUsbCiCallbacks();
      _ciCallbacks.OnApdu = new OnSmarDtvUsbCiApdu(OnApdu);
      _ciCallbacks.OnApplicationInfo = new OnSmarDtvUsbCiApplicationInfo(OnApplicationInfo);
      _ciCallbacks.OnCiState = new OnSmarDtvUsbCiState(OnCiState);
      _ciCallbacks.OnCloseMmi = new OnSmarDtvUsbCiCloseMmi(OnCloseMmi);
      _ciCallbackBuffer = Marshal.AllocCoTaskMem(CI_CALLBACKS_SIZE);
      Marshal.StructureToPtr(_ciCallbacks, _ciCallbackBuffer, true);
      int hr = (int)_ciType.GetMethod("USB2CI_Init").Invoke(_ciFilter, new object[] { _ciCallbackBuffer });
      if (hr == (int)HResult.Severity.Success)
      {
        IntPtr versionInfoBuffer = Marshal.AllocCoTaskMem(VERSION_INFO_SIZE);
        for (byte i = 0; i < VERSION_INFO_SIZE; i++)
        {
          Marshal.WriteByte(versionInfoBuffer, i, 0);
        }
        hr = (int)_ciType.GetMethod("USB2CI_GetVersion").Invoke(_ciFilter, new object [] { versionInfoBuffer });
        if (hr == (int)HResult.Severity.Success)
        {
          //DVB_MMI.DumpBinary(versionBuffer, 0, VERSION_INFO_SIZE);
          VersionInfo versionInfo = (VersionInfo)Marshal.PtrToStructure(versionInfoBuffer, typeof(VersionInfo));
          this.LogDebug("  plugin version     = {0}", versionInfo.PluginVersion);
          this.LogDebug("  BDA driver version = {0}", versionInfo.BdaVersion);
          this.LogDebug("  USB driver version = {0}", versionInfo.UsbVersion);
          this.LogDebug("  firmware version   = {0}", versionInfo.FirmwareVersion);
          this.LogDebug("  FPGA version       = {0}", versionInfo.FpgaVersion);
        }
        else
        {
          this.LogDebug("SmarDTV USB CI: failed to retrieve version information, hr = 0x{0:x} ({1})", hr, HResult.GetDXErrorString(hr));
        }
        Marshal.FreeCoTaskMem(versionInfoBuffer);

        this.LogDebug("SmarDTV USB CI: result = success");
        return true;
      }

      this.LogDebug("SmarDTV USB CI: result = failure, hr = 0x{0:x} ({1})", hr, HResult.GetDXErrorString(hr));
      return false;
    }

    /// <summary>
    /// Close the conditional access interface.
    /// </summary>
    /// <returns><c>true</c> if the interface is successfully closed, otherwise <c>false</c></returns>
    public bool CloseInterface()
    {
      this.LogDebug("SmarDTV USB CI: close conditional access interface");

      _isCamPresent = false;
      _isCamReady = false;
      if (_ciCallbackBuffer != IntPtr.Zero)
      {
        Marshal.Release(_ciCallbackBuffer);
        _ciCallbackBuffer = IntPtr.Zero;
      }

      this.LogDebug("SmarDTV USB CI: result = success");
      return true;
    }

    /// <summary>
    /// Reset the conditional access interface.
    /// </summary>
    /// <param name="resetDevice">This parameter will be set to <c>true</c> if the device must be reset
    ///   for the interface to be completely and successfully reset.</param>
    /// <returns><c>true</c> if the interface is successfully reset, otherwise <c>false</c></returns>
    public bool ResetInterface(out bool resetDevice)
    {
      resetDevice = true;
      return true;
    }

    /// <summary>
    /// Determine whether the conditional access interface is ready to receive commands.
    /// </summary>
    /// <returns><c>true</c> if the interface is ready, otherwise <c>false</c></returns>
    public bool IsInterfaceReady()
    {
      this.LogDebug("SmarDTV USB CI: is conditional access interface ready");

      // The CI/CAM state is automatically updated in the OnCiState() callback.
      this.LogDebug("SmarDTV USB CI: result = {0}", _isCamReady);
      return _isCamReady;
    }

    /// <summary>
    /// Send a command to to the conditional access interface.
    /// </summary>
    /// <param name="channel">The channel information associated with the service which the command relates to.</param>
    /// <param name="listAction">It is assumed that the interface may be able to decrypt one or more services
    ///   simultaneously. This parameter gives the interface an indication of the number of services that it
    ///   will be expected to manage.</param>
    /// <param name="command">The type of command.</param>
    /// <param name="pmt">The programme map table for the service.</param>
    /// <param name="cat">The conditional access table for the service.</param>
    /// <returns><c>true</c> if the command is successfully sent, otherwise <c>false</c></returns>
    public bool SendCommand(IChannel channel, CaPmtListManagementAction listAction, CaPmtCommand command, Pmt pmt, Cat cat)
    {
      this.LogDebug("SmarDTV USB CI: send conditional access command, list action = {0}, command = {1}", listAction, command);

      if (!_isSmarDtvUsbCi)
      {
        this.LogDebug("SmarDTV USB CI: device not initialised or interface not supported");
        return false;
      }
      if (_ciFilter == null)
      {
        this.LogDebug("SmarDTV USB CI: device filter not added to the BDA filter graph");
        return false;
      }
      if (_ciCallbackBuffer == IntPtr.Zero)
      {
        this.LogDebug("SmarDTV USB CI: CA interface is not open");
        return false;
      }
      if (command == CaPmtCommand.OkMmi || command == CaPmtCommand.Query)
      {
        this.LogDebug("SmarDTV USB CI: command type {0} is not supported", command);
        return false;
      }
      if (pmt == null)
      {
        this.LogDebug("SmarDTV USB CI: PMT not supplied");
        return true;
      }

      // "Not selected" commands do nothing.
      if (command == CaPmtCommand.NotSelected)
      {
        this.LogDebug("SmarDTV USB CI: result = success");
        return true;
      }

      // During development of this class I tried a couple of tricks to get the WinTV-CI to decrypt
      // more than one channel at a time. I came to the conclusion that it is not possible. I tried:
      // - assemble a fake CA PMT structure and pass it to USB2CI_GuiSendPMT
      // - send a CA PMT APDU using USB2CI_APDUToCAM
      // We'll just send this PMT to the CAM regardless of the list management action.
      ReadOnlyCollection<byte> rawPmt = pmt.GetRawPmt();
      byte[] rawPmtCopy = new byte[rawPmt.Count];
      rawPmt.CopyTo(rawPmtCopy, 0);
      int hr = (int)_ciType.GetMethod("USB2CI_GuiSendPMT").Invoke(_ciFilter, new object[] { rawPmtCopy, (short)rawPmt.Count });
      if (hr == (int)HResult.Severity.Success)
      {
        this.LogDebug("SmarDTV USB CI: result = success");
        return true;
      }

      this.LogDebug("SmarDTV USB CI: result = failure, hr = 0x{0:x} ({1})", hr, HResult.GetDXErrorString(hr));
      return false;
    }

    #endregion

    #region ICiMenuActions members

    /// <summary>
    /// Set the CAM callback handler functions.
    /// </summary>
    /// <param name="ciMenuHandler">A set of callback handler functions.</param>
    /// <returns><c>true</c> if the handlers are set, otherwise <c>false</c></returns>
    public bool SetCiMenuHandler(ICiMenuCallbacks ciMenuHandler)
    {
      if (ciMenuHandler != null)
      {
        _ciMenuCallbacks = ciMenuHandler;
        return true;
      }
      return false;
    }

    /// <summary>
    /// Send a request from the user to the CAM to open the menu.
    /// </summary>
    /// <returns><c>true</c> if the request is successfully passed to and processed by the CAM, otherwise <c>false</c></returns>
    public bool EnterCIMenu()
    {
      this.LogDebug("SmarDTV USB CI: enter menu");

      if (!_isSmarDtvUsbCi)
      {
        this.LogDebug("SmarDTV USB CI: device not initialised or interface not supported");
        return false;
      }
      if (_ciFilter == null)
      {
        this.LogDebug("SmarDTV USB CI: device filter not added to the BDA filter graph");
        return false;
      }
      if (!_isCamReady)
      {
        this.LogDebug("SmarDTV USB CI: the CAM is not ready");
        return false;
      }

      int hr = (int)_ciType.GetMethod("USB2CI_OpenMMI").Invoke(_ciFilter, null);
      if (hr == (int)HResult.Severity.Success)
      {
        this.LogDebug("SmarDTV USB CI: result = success");
        return true;
      }

      this.LogDebug("SmarDTV USB CI: result = failure, hr = 0x{0:x} ({1})", hr, HResult.GetDXErrorString(hr));
      return false;
    }

    /// <summary>
    /// Send a request from the user to the CAM to close the menu.
    /// </summary>
    /// <returns><c>true</c> if the request is successfully passed to and processed by the CAM, otherwise <c>false</c></returns>
    public bool CloseCIMenu()
    {
      this.LogDebug("SmarDTV USB CI: close menu");

      if (!_isSmarDtvUsbCi)
      {
        this.LogDebug("SmarDTV USB CI: device not initialised or interface not supported");
        return false;
      }
      if (_ciFilter == null)
      {
        this.LogDebug("SmarDTV USB CI: device filter not added to the BDA filter graph");
        return false;
      }
      if (!_isCamReady)
      {
        this.LogDebug("SmarDTV USB CI: the CAM is not ready");
        return false;
      }

      byte[] apdu = DvbMmiHandler.CreateMmiClose(0);
      int hr = (int)_ciType.GetMethod("USB2CI_APDUToCAM").Invoke(_ciFilter, new object[] { apdu.Length, apdu });
      if (hr == (int)HResult.Severity.Success)
      {
        this.LogDebug("SmarDTV USB CI: result = success");
        return true;
      }

      this.LogDebug("SmarDTV USB CI: result = failure, hr = 0x{0:x} ({1})", hr, HResult.GetDXErrorString(hr));
      return false;
    }

    /// <summary>
    /// Send a menu entry selection from the user to the CAM.
    /// </summary>
    /// <param name="choice">The index of the selection as an unsigned byte value.</param>
    /// <returns><c>true</c> if the selection is successfully passed to and processed by the CAM, otherwise <c>false</c></returns>
    public bool SelectMenu(byte choice)
    {
      this.LogDebug("SmarDTV USB CI: select menu entry, choice = {0}", choice);

      if (!_isSmarDtvUsbCi)
      {
        this.LogDebug("SmarDTV USB CI: device not initialised or interface not supported");
        return false;
      }
      if (_ciFilter == null)
      {
        this.LogDebug("SmarDTV USB CI: device filter not added to the BDA filter graph");
        return false;
      }
      if (!_isCamReady)
      {
        this.LogDebug("SmarDTV USB CI: the CAM is not ready");
        return false;
      }

      byte[] apdu = DvbMmiHandler.CreateMmiMenuAnswer(choice);
      int hr = (int)_ciType.GetMethod("USB2CI_APDUToCAM").Invoke(_ciFilter, new object[] { apdu.Length, apdu });
      if (hr == (int)HResult.Severity.Success)
      {
        this.LogDebug("SmarDTV USB CI: result = success");
        return true;
      }

      this.LogDebug("SmarDTV USB CI: result = failure, hr = 0x{0:x} ({1})", hr, HResult.GetDXErrorString(hr));
      return false;
    }

    /// <summary>
    /// Send a response from the user to the CAM.
    /// </summary>
    /// <param name="cancel"><c>True</c> to cancel the request.</param>
    /// <param name="answer">The user's response.</param>
    /// <returns><c>true</c> if the response is successfully passed to and processed by the CAM, otherwise <c>false</c></returns>
    public bool SendMenuAnswer(bool cancel, string answer)
    {
      if (answer == null)
      {
        answer = string.Empty;
      }
      this.LogDebug("SmarDTV USB CI: send menu answer, answer = {0}, cancel = {1}", answer, cancel);

      if (!_isSmarDtvUsbCi)
      {
        this.LogDebug("SmarDTV USB CI: device not initialised or interface not supported");
        return false;
      }
      if (_ciFilter == null)
      {
        this.LogDebug("SmarDTV USB CI: device filter not added to the BDA filter graph");
        return false;
      }
      if (!_isCamReady)
      {
        this.LogDebug("SmarDTV USB CI: the CAM is not ready");
        return false;
      }

      MmiResponseType responseType = MmiResponseType.Answer;
      if (cancel)
      {
        responseType = MmiResponseType.Cancel;
      }
      byte[] apdu = DvbMmiHandler.CreateMmiEnquiryAnswer(responseType, answer);
      int hr = (int)_ciType.GetMethod("USB2CI_APDUToCAM").Invoke(_ciFilter, new object[] { apdu.Length, apdu });
      if (hr == (int)HResult.Severity.Success)
      {
        this.LogDebug("SmarDTV USB CI: result = success");
        return true;
      }

      this.LogDebug("SmarDTV USB CI: result = failure, hr = 0x{0:x} ({1})", hr, HResult.GetDXErrorString(hr));
      return false;
    }

    #endregion

    #region IDisposable member

    /// <summary>
    /// Release and dispose all resources.
    /// </summary>
    public override void Dispose()
    {
      CloseInterface();
      if (_graph != null)
      {
        _graph.RemoveFilter(_ciFilter);
        Release.ComObject("SmarDTV CI graph", ref _graph);
      }
      Release.ComObject("SmarDTV CI filter", ref _ciFilter);

      if (_ciDevice != null)
      {
        lock (_devicesInUse)
        {
          _devicesInUse.Remove(_ciDevice.DevicePath);
        }
        _ciDevice.Dispose();
        _ciDevice = null;
      }

      _isSmarDtvUsbCi = false;
    }

    #endregion
  }
}