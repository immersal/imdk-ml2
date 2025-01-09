/*===============================================================================
Copyright (C) 2023 Immersal - Part of Hexagon. All Rights Reserved.

This file is part of the Immersal SDK.

The Immersal SDK cannot be copied, distributed, or made available to
third-parties for commercial purposes without written permission of Immersal Ltd.

Contact sales@immersal.com for licensing requests.
===============================================================================*/

#if IMMERSAL_MAGIC_LEAP_ENABLED
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.XR.MagicLeap;

using Immersal.XR;
using Unity.Collections.LowLevel.Unsafe;

namespace Immersal.XR.MagicLeap
{
    public class MagicLeapSupport : MonoBehaviour, IPlatformSupport
    {
        [SerializeField]
        private bool verboseDebugLogging;
        
        public bool IsCameraConnected => captureCamera != null && captureCamera.ConnectionEstablished;

        private Camera m_Camera;

        // Magic Leap
        private MLCamera captureCamera;
        private GraphicsFormat pngFormat = GraphicsFormat.R8_UNorm;
        private MLCamera.CaptureType captureType = MLCamera.CaptureType.Video;
        private List<MLCamera.StreamCapability> streamCapabilities;
        private readonly MLPermissions.Callbacks permissionCallbacks = new MLPermissions.Callbacks();
        
        // ML2 data captured in ML2 update loop
        private MLCamera.CameraOutput m_CapturedFrameInfo;
        private MLCamera.PlaneInfo m_CapturedYUVPlane;
        private MLCamera.IntrinsicCalibrationParameters? m_CapturedIntrinsics;
        
        private Vector3 m_CapturedCameraPos;
        private Quaternion m_CapturedCameraRot;
        
        //private byte[] pixelBuffer;

        private IPlatformConfiguration m_Configuration;
        private bool m_CameraDeviceAvailable;
        private bool m_IsCapturingVideo;
        private bool m_ConfigDone;
        private bool m_CameraConfigDone;
        private bool m_CameraIsReady;
        
        private Task<(bool, CameraData)> m_CurrentCameraDataTask;
        private bool m_isTracking = false;
        
        public async Task<IPlatformUpdateResult> UpdatePlatform()
        {
            return await UpdateWithConfiguration(m_Configuration);
        }
        
        public async Task<IPlatformUpdateResult> UpdatePlatform(IPlatformConfiguration oneShotConfiguration)
        {
            return await UpdateWithConfiguration(oneShotConfiguration);
        }
        
        private async Task<IPlatformUpdateResult> UpdateWithConfiguration(IPlatformConfiguration configuration)
        {
            Debug.Log("ML2-UpdatePlatform");
            if (!m_ConfigDone)
                throw new ComponentTaskCriticalException("Trying to update platform before configuration.");
            
            // Status
            SimplePlatformStatus platformStatus = new SimplePlatformStatus
            {
                TrackingQuality = m_isTracking ? 1 : 0
            };

            m_CurrentCameraDataTask = GetCameraData();
            (bool success, CameraData data) = await m_CurrentCameraDataTask;

            // UpdateResult
            SimplePlatformUpdateResult r = new SimplePlatformUpdateResult()
            {
                Success = success,
                Status = platformStatus,
                CameraData = data
            };

            return r;
        }
        
        private async Task<(bool, CameraData)> GetCameraData()
        {
            if (m_CapturedYUVPlane.Data is null || m_CapturedYUVPlane.Data.Length <= 0) return (false, null);
            if (!TryAcquireIntrinsics(out Vector4 intrinsics, out double[] distortion)) return (false, null);

            MagicLeapImageData imageData = new MagicLeapImageData(m_CapturedYUVPlane);
            CameraData data = new CameraData(imageData)
            {
                CameraPositionOnCapture = m_CapturedCameraPos,
                CameraRotationOnCapture = m_CapturedCameraRot,
                Intrinsics = intrinsics,
                Distortion = distortion,
                Width = (int)m_CapturedYUVPlane.Width,
                Height = (int)m_CapturedYUVPlane.Height,
                Orientation = GetOrientation()
            };

            return (true, data);
        }

        private Quaternion GetOrientation()
        {
            return Quaternion.Euler(0f, 0f, 180.0f);
        }
        
        public async Task<IPlatformConfigureResult> ConfigurePlatform()
        {
            PlatformConfiguration config = new PlatformConfiguration
            {
                CameraDataFormat = CameraDataFormat.SingleChannel
            };
            return await ConfigurePlatform(config);
        }

        public async Task<IPlatformConfigureResult> ConfigurePlatform(IPlatformConfiguration configuration)
        {
            m_ConfigDone = await ConfigureML();
            
            IPlatformConfigureResult r = new SimplePlatformConfigureResult
            { 
                Success = m_ConfigDone
            };
            
            return r;
        }

        public Task StopAndCleanUp()
        {
            return Task.Run(DisconnectCamera);
        }
        
        private async Task<bool> ConfigureML()
        {
            m_Camera = Camera.main;
            
            permissionCallbacks.OnPermissionGranted += OnPermissionGranted;
            permissionCallbacks.OnPermissionDenied += OnPermissionDenied;
            permissionCallbacks.OnPermissionDeniedAndDontAskAgain += OnPermissionDenied;

            m_IsCapturingVideo = false;

            // Start the camera preparation process
            MLPermissions.RequestPermission(MLPermission.Camera, permissionCallbacks);

            // Wait until camera is ready
            while (!m_CameraConfigDone)
            {
                await Task.Yield();
            }
            
            return m_CameraIsReady;
        }
        
        private void OnPermissionGranted(string permission)
        {
#if UNITY_ANDROID
            MLPluginLog.Debug($"Granted {permission}.");
            if (permission != MLPermission.Camera)
                return;
            TryEnableMLCamera();
#endif
        }

        private void OnPermissionDenied(string permission)
        {
            if (permission == MLPermission.Camera)
            {
#if UNITY_ANDROID
                MLPluginLog.Error($"{permission} denied.");
#endif
            }
        }
        
        private void TryEnableMLCamera()
        {
            if (!MLPermissions.CheckPermission(MLPermission.Camera).IsOk)
            {
                m_CameraConfigDone = true;
                return;
            }
            StartCoroutine(EnableMLCamera());
        }
        
        /// <summary>
        /// Connects the MLCamera component and instantiates a new instance
        /// if it was never created.
        /// </summary>
        private IEnumerator EnableMLCamera()
        {
            while (!m_CameraDeviceAvailable)
            {
                MLResult result =
                    MLCamera.GetDeviceAvailabilityStatus(MLCamera.Identifier.CV, out m_CameraDeviceAvailable);
                if (!(result.IsOk && m_CameraDeviceAvailable))
                {
                    // Wait until camera device is available
                    yield return new WaitForSeconds(1.0f);
                }
            }

            Log("Camera device available");

            yield return new WaitForSeconds(1.0f);
            ConnectCamera();

            yield return new WaitForSeconds(1.0f);
            StartVideoCapture();
        }
        
        /// <summary>
        /// Connects to the MLCamera.
        /// </summary>
        private void ConnectCamera()
        {
            MLCamera.ConnectContext context = MLCamera.ConnectContext.Create();
            context.CamId = MLCamera.Identifier.CV;
            context.Flags = MLCamera.ConnectFlag.CamOnly;
            context.EnableVideoStabilization = true;

            captureCamera = MLCamera.CreateAndConnect(context);

            if (captureCamera != null)
            {
                if (GetImageStreamCapabilities())
                {
                    captureCamera.OnRawVideoFrameAvailable += OnCaptureRawVideoFrameAvailable;
                }
            }
        }

        /// <summary>
        /// Disconnects the camera.
        /// </summary>
        private void DisconnectCamera()
        {
            if (captureCamera == null || !IsCameraConnected)
                return;
            
            captureCamera.OnRawVideoFrameAvailable -= OnCaptureRawVideoFrameAvailable;
            captureCamera.Disconnect();
            streamCapabilities = null;
            m_CameraIsReady = false;
        }

        private void StartVideoCapture()
        {
            MLCamera.CaptureConfig captureConfig = new MLCamera.CaptureConfig();
            captureConfig.CaptureFrameRate = MLCamera.CaptureFrameRate._30FPS;
            captureConfig.StreamConfigs = new MLCamera.CaptureStreamConfig[1];
            captureConfig.StreamConfigs[0] =
                MLCamera.CaptureStreamConfig.Create(GetStreamCapability(), MLCamera.OutputFormat.YUV_420_888);

            MLResult result = captureCamera.PrepareCapture(captureConfig, out MLCamera.Metadata _);

            if (MLResult.DidNativeCallSucceed(result.Result, nameof(captureCamera.PrepareCapture)))
            {
                captureCamera.PreCaptureAEAWB();

                if (captureType == MLCamera.CaptureType.Video)
                {
                    result = captureCamera.CaptureVideoStart();
                    m_IsCapturingVideo = MLResult.DidNativeCallSucceed(result.Result, nameof(captureCamera.CaptureVideoStart));
                }
            }
        }
        
        /// <summary>
        /// Gets currently selected StreamCapability
        /// </summary>
        private MLCamera.StreamCapability GetStreamCapability()
        {
            foreach (var streamCapability in streamCapabilities.Where(s => s.CaptureType == captureType))
            {
                Log(streamCapability.ToString());
                // option: 640x480, 1280x720, 1920x1080, 3840x2160
//                if (streamCapability.Width == 1920 && streamCapability.Height == 1080)
//                if (streamCapability.Width == 1280 && streamCapability.Height == 960)
                if (streamCapability.Width == 3840 && streamCapability.Height == 2160)
                {
                    return streamCapability;
                }
            }
            Log($"{streamCapabilities.ToString()} is selected!");
            return streamCapabilities[0];
        }

        /// <summary>
        /// Gets the Image stream capabilities.
        /// </summary>
        /// <returns>True if MLCamera returned at least one stream capability.</returns>
        private bool GetImageStreamCapabilities()
        {
            var result =
                captureCamera.GetStreamCapabilities(out MLCamera.StreamCapabilitiesInfo[] streamCapabilitiesInfo);

            if (!result.IsOk)
            {
                Log("Could not get Stream capabilities Info.");
                return false;
            }

            streamCapabilities = new List<MLCamera.StreamCapability>();

            for (int i = 0; i < streamCapabilitiesInfo.Length; i++)
            {
                foreach (var streamCap in streamCapabilitiesInfo[i].StreamCapabilities)
                {
                    streamCapabilities.Add(streamCap);
                }
            }

            return streamCapabilities.Count > 0;
        }
        
        /// <summary>
        /// Handles the event of a new image getting captured.
        /// </summary>
        /// <param name="capturedFrame">Captured Frame.</param>
        /// <param name="resultExtras">Result Extra.</param>
        private void OnCaptureRawVideoFrameAvailable(MLCamera.CameraOutput capturedFrame,
            MLCamera.ResultExtras resultExtras,
            MLCamera.Metadata metadata)
        {
            m_CameraIsReady = m_CameraConfigDone = true;
            m_CapturedFrameInfo = capturedFrame;
            m_CapturedYUVPlane = m_CapturedFrameInfo.Planes[0];
            m_CapturedCameraPos = m_Camera.transform.position;
            m_CapturedCameraRot = m_Camera.transform.rotation;
            m_CapturedIntrinsics = resultExtras.Intrinsics;
        }
        
        private bool TryAcquireIntrinsics(out Vector4 intr, out double[] dist)
        {
            intr = Vector4.zero;
            dist = new double[5];

            if (!(this.m_CapturedIntrinsics is MLCamera.IntrinsicCalibrationParameters intrinsicsValue))
            {
                return false;
            }

            if (!m_IsCapturingVideo) return false;

            //DisplayData(intrinsicsValue);

            intr.x = intrinsicsValue.FocalLength.x;
            intr.y = intrinsicsValue.FocalLength.y;
            intr.z = intrinsicsValue.PrincipalPoint.x;
            intr.w = intrinsicsValue.PrincipalPoint.y;
            dist = intrinsicsValue.Distortion;

            return true;
        }
        
        void DisplayData(MLCamera.IntrinsicCalibrationParameters cameraParameters)
        {
            Debug.LogFormat("Width: {0}", cameraParameters.Width);
            Debug.LogFormat("Height: {0}", cameraParameters.Height);
            Debug.LogFormat("FocalLength: {0}", cameraParameters.FocalLength);
            Debug.LogFormat("PrincipalPoint: {0}", cameraParameters.PrincipalPoint);
            Debug.LogFormat("FOV: {0}", cameraParameters.FOV);
            int index = 0;
            foreach (double dist in cameraParameters.Distortion)
            {
                Debug.LogFormat("Distortion({0}): {1}", index, dist);
                index++;
            }
        }

        private void OnDisable()
        {
            DisconnectCamera();
        }
        
        private void Log(string message)
        {
            if (verboseDebugLogging) Debug.Log($"MLCDP: {message}");
        }
        
    }

    public class MagicLeapImageData : ImageData
    {
        public override IntPtr UnmanagedDataPointer => m_UnmanagedDataPointer;
        public override byte[] ManagedBytes => m_Bytes;

        private byte[] m_Bytes;
        private IntPtr m_UnmanagedDataPointer;
        private GCHandle m_managedDataHandle;

        private const bool m_InvertVertically = false;
        
        public MagicLeapImageData(MLCamera.PlaneInfo yBuffer)
        {
            byte[] data = yBuffer.Data;
            int width = (int)yBuffer.Width, height = (int)yBuffer.Height, size = width * height;
            int stride = m_InvertVertically ? -(int)yBuffer.Stride : (int)yBuffer.Stride;
            int invertStartOffset = ((int)yBuffer.Stride * height) - (int)yBuffer.Stride;

            m_Bytes = new byte[size];
            m_managedDataHandle = GCHandle.Alloc(m_Bytes, GCHandleType.Pinned);

            unsafe
            {
                fixed (byte* pinnedData = data, dstPtr = m_Bytes)
                {
                    byte* srcPtr = m_InvertVertically ? pinnedData + invertStartOffset : pinnedData;
                    if (width > 0 && height > 0) {
                        UnsafeUtility.MemCpyStride(dstPtr, width, srcPtr, stride, width, height);
                    }
                }
            }

            m_UnmanagedDataPointer = m_managedDataHandle.AddrOfPinnedObject();
        }
        
        public override void DisposeData()
        {
            m_managedDataHandle.Free();
            m_UnmanagedDataPointer = IntPtr.Zero;
        }
    }

}
#endif