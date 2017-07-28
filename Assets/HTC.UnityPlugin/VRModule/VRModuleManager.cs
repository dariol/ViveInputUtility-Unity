﻿//========= Copyright 2016-2017, HTC Corporation. All rights reserved. ===========

using HTC.UnityPlugin.Utility;
using System.Collections.Generic;
using UnityEngine;

namespace HTC.UnityPlugin.VRModuleManagement
{
    public partial class VRModule : SingletonBehaviour<VRModule>
    {
        private static readonly DeviceState s_defaultState;
        private static readonly ModuleBase[] s_modules;
        private static readonly Dictionary<string, uint> s_deviceSerialNumberTable = new Dictionary<string, uint>((int)MAX_DEVICE_COUNT);

        [SerializeField]
        private bool m_dontDestroyOnLoad = true;
        [SerializeField]
        private bool m_lockPhysicsUpdateRateToRenderFrequency = true;
        [SerializeField]
        private SelectVRModuleEnum m_selectModule = SelectVRModuleEnum.Auto;
        [SerializeField]
        private VRModuleTrackingSpaceType m_trackingSpaceType = VRModuleTrackingSpaceType.RoomScale;

        [SerializeField]
        private NewPosesEvent m_onNewPoses = new NewPosesEvent();
        [SerializeField]
        private ControllerRoleChangedEvent m_onControllerRoleChanged = new ControllerRoleChangedEvent();
        [SerializeField]
        private InputFocusEvent m_onInputFocus = new InputFocusEvent();
        [SerializeField]
        private DeviceConnectedEvent m_onDeviceConnected = new DeviceConnectedEvent();
        [SerializeField]
        private ModuleActivatedEvent m_onModuleActivated = new ModuleActivatedEvent();
        [SerializeField]
        private ModuleDeactivatedEvent m_onModuleDeactivated = new ModuleDeactivatedEvent();

        private int m_poseUpdatedFrame = -1;
        private bool m_isUpdating = false;
        private bool m_isDestoryed = false;

        private SupportedVRModule m_activatedModule = SupportedVRModule.Uninitialized;
        private ModuleBase m_activatedModuleBase;
        private DeviceState[] m_prevStates;
        private DeviceState[] m_currStates;

        static VRModule()
        {
            s_modules = new ModuleBase[EnumUtils.GetMaxValue(typeof(SupportedVRModule)) + 1];
            s_modules[(int)SupportedVRModule.None] = new DefaultModule();
            //s_modules[(int)SupportedModule.Simulator] = new DefaultModule();
            s_modules[(int)SupportedVRModule.UnityNativeVR] = new UnityEngineVRModule();
            s_modules[(int)SupportedVRModule.SteamVR] = new SteamVRModule();
            s_modules[(int)SupportedVRModule.OculusVR] = new OculusVRModule();

            s_defaultState = new DeviceState(INVALID_DEVICE_INDEX);
            s_defaultState.Reset();
        }

        protected override void OnSingletonBehaviourInitialized()
        {
            if (m_dontDestroyOnLoad && transform.parent == null && Application.isPlaying)
            {
                DontDestroyOnLoad(gameObject);
            }

            m_currStates = new DeviceState[MAX_DEVICE_COUNT];
            for (var i = 0u; i < MAX_DEVICE_COUNT; ++i) { m_currStates[i] = new DeviceState(i); }

            m_prevStates = new DeviceState[MAX_DEVICE_COUNT];
            for (var i = 0u; i < MAX_DEVICE_COUNT; ++i) { m_prevStates[i] = new DeviceState(i); }

            Camera.onPreCull += OnCameraPreCull;
        }

        protected override void OnDestroy()
        {
            if (IsInstance)
            {
                Camera.onPreCull -= OnCameraPreCull;

                m_isDestoryed = true;

                if (!m_isUpdating)
                {
                    CleanUp();
                }
            }

            base.OnDestroy();
        }

        private void OnCameraPreCull(Camera cam)
        {
#if UNITY_EDITOR
            // skip pre cull from scene camera (editor only?)
            // because at this point, the LastPoses seems not updated yet (the result is same as last frame)
            // shell wait till next game camera pre cull (top game camera)
            if (cam.depth == 0 && cam.name == "SceneCamera") { return; }
#endif
            // update only once per frame
            if (!ChangeProp.Set(ref m_poseUpdatedFrame, Time.renderedFrameCount)) { return; }

            m_isUpdating = true;

            UpdateActivatedModule();

            UpdateDeviceStates();

            if (m_isDestoryed)
            {
                CleanUp();
            }

            m_isUpdating = false;
        }

        private static SupportedVRModule GetSelectedModule(SelectVRModuleEnum select)
        {
            if (select == SelectVRModuleEnum.Auto)
            {
                for (int i = s_modules.Length - 1; i >= 0; --i)
                {
                    if (s_modules[i] != null && s_modules[i].ShouldActiveModule())
                    {
                        return (SupportedVRModule)i;
                    }
                }
            }
            else if ((int)select >= 0 && (int)select < s_modules.Length)
            {
                return (SupportedVRModule)select;
            }
            else
            {
                return SupportedVRModule.None;
            }

            return SupportedVRModule.Uninitialized;
        }

        private void UpdateActivatedModule()
        {
            var currentSelectedModule = GetSelectedModule(m_selectModule);

            if (m_activatedModule == currentSelectedModule) { return; }

            // clean up if previous active module is not null
            if (m_activatedModule != SupportedVRModule.Uninitialized)
            {
                CleanUp();
                // m_activatedModule will reset to SupportedVRModule.Uninitialized after CleanUp()
            }

            // activate the selected module
            if (currentSelectedModule != SupportedVRModule.Uninitialized)
            {
                m_activatedModule = currentSelectedModule;
                m_activatedModuleBase = s_modules[(int)currentSelectedModule];
                m_activatedModuleBase.OnActivated();

                VRModule.InvokeModuleActivatedEvent(m_activatedModule);
            }

            // update module
            if (currentSelectedModule != SupportedVRModule.Uninitialized)
            {
                m_activatedModuleBase.Update();
            }
        }

        private void UpdateDeviceStates()
        {
            // copy status to from current state to previous state
            for (var i = 0u; i < MAX_DEVICE_COUNT; ++i)
            {
                if (m_prevStates[i].isConnected || m_currStates[i].isConnected)
                {
                    m_prevStates[i].CopyFrom(m_currStates[i]);
                }
            }

            // update status
            if (m_activatedModuleBase != null)
            {
                m_activatedModuleBase.UpdateDeviceState(m_prevStates, m_currStates);
            }

            // send connect/disconnect event
            for (var i = 0u; i < MAX_DEVICE_COUNT; ++i)
            {
                if (m_prevStates[i].isConnected != m_currStates[i].isConnected)
                {
                    if (m_currStates[i].isConnected)
                    {
                        try
                        {
                            s_deviceSerialNumberTable.Add(m_currStates[i].serialNumber, i);
                        }
                        catch (System.Exception e)
                        {
                            Debug.LogError(e.ToString());
                        }
                    }
                    else
                    {
                        s_deviceSerialNumberTable.Remove(m_prevStates[i].serialNumber);
                    }

                    VRModule.InvokeDeviceConnectedEvent(i, m_currStates[i].isConnected);
                }
            }

            // send new poses event
            VRModule.InvokeNewPosesEvent();
        }

        private void CleanUp()
        {
            // copy status to from current state to previous state, and reset current state
            for (var i = 0u; i < MAX_DEVICE_COUNT; ++i)
            {
                if (m_prevStates[i].isConnected || m_currStates[i].isConnected)
                {
                    m_prevStates[i].CopyFrom(m_currStates[i]);
                    m_currStates[i].Reset();
                }
            }

            // send disconnect event
            for (var i = 0u; i < MAX_DEVICE_COUNT; ++i)
            {
                if (m_prevStates[i].isConnected)
                {
                    VRModule.InvokeDeviceConnectedEvent(i, false);
                }
            }

            if (m_activatedModuleBase != null)
            {
                var deactivatedModule = m_activatedModule;
                var deactivatedModuleBase = m_activatedModuleBase;

                m_activatedModule = SupportedVRModule.Uninitialized;
                m_activatedModuleBase = null;

                deactivatedModuleBase.OnDeactivated();

                VRModule.InvokeModuleDeactivatedEvent(deactivatedModule);
            }
        }
    }
}