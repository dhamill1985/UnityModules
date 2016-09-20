﻿using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif
using System;
using System.Collections;
using Leap.Unity.Attributes;
using Leap.Unity.Graphing;

namespace Leap.Unity {
  /**LeapServiceProvider creates a Controller and supplies Leap Hands and images */
  public class LeapServiceProvider : LeapProvider {
    /** Conversion factor for nanoseconds to seconds. */
    protected const float NS_TO_S = 1e-6f;
    /** Conversion factor for seconds to nanoseconds. */
    protected const float S_TO_NS = 1e6f;

    [Tooltip("Set true if the Leap Motion hardware is mounted on an HMD; otherwise, leave false.")]
    [SerializeField]
    protected bool _isHeadMounted = false;

    [AutoFind]
    [SerializeField]
    protected LeapVRTemporalWarping _temporalWarping;

    [Tooltip("When true, update frames will be re-used for physics.  This is an optimization, since the total number " +
             "of frames that need to be calculated is halved.  However, this introduces extra latency and inaccuracy " +
             "into the physics frames.")]
    [SerializeField]
    protected bool _reuseFramesForPhysics = false;

    [Header("Device Type")]
    [SerializeField]
    protected bool _overrideDeviceType = false;

    [Tooltip("If overrideDeviceType is enabled, the hand controller will return a device of this type.")]
    [SerializeField]
    protected LeapDeviceType _overrideDeviceTypeWith = LeapDeviceType.Peripheral;

    [Header("Interpolation")]
    [Tooltip("Interpolate frames to deliver smoother motion.")]
    [SerializeField]
    protected bool _useInterpolation = true;

    [Tooltip("How much delay should be added to interpolation.")]
    protected long _interpolationDelay = 0;

    protected Controller leap_controller_;

    protected bool manualUpdateHasBeenCalledSinceUpdate;
    protected Vector3 warpedPosition;
    protected Quaternion warpedRotation;
    protected SmoothedFloat _fixedOffset = new SmoothedFloat();
    protected SmoothedFloat _smoothedTrackingLatency = new SmoothedFloat();

    protected Frame _untransformedUpdateFrame;
    protected Frame _transformedUpdateFrame;

    protected Frame _untransformedFixedFrame;
    protected Frame _transformedFixedFrame;

    protected Frame _untransformedPreCullFrame;
    protected Frame _transformedPreCullFrame;

    protected Image _currentImage;

    protected Matrix4x4[] _transformArray = new Matrix4x4[6];

    public override Frame CurrentFrame {
      get {
        return _transformedUpdateFrame;
      }
    }

    public override Image CurrentImage {
      get {
        return _currentImage;
      }
    }

    public override Frame CurrentFixedFrame {
      get {
        if (_reuseFramesForPhysics) {
          return _transformedUpdateFrame;
        } else {
          return _transformedFixedFrame;
        }
      }
    }

    public bool UseInterpolation {
      get {
        return _useInterpolation;
      }
      set {
        _useInterpolation = value;
      }
    }

    public long InterpolationDelay {
      get {
        return _interpolationDelay;
      }
      set {
        _interpolationDelay = value;
      }
    }

    /** Returns the Leap Controller instance. */
    public Controller GetLeapController() {
#if UNITY_EDITOR
      //Null check to deal with hot reloading
      if (leap_controller_ == null) {
        createController();
      }
#endif
      return leap_controller_;
    }

    /** True, if the Leap Motion hardware is plugged in and this application is connected to the Leap Motion service. */
    public bool IsConnected() {
      return GetLeapController().IsConnected;
    }

    /** Returns information describing the device hardware. */
    public LeapDeviceInfo GetDeviceInfo() {
      if (_overrideDeviceType) {
        return new LeapDeviceInfo(_overrideDeviceTypeWith);
      }

      DeviceList devices = GetLeapController().Devices;
      if (devices.Count == 1) {
        LeapDeviceInfo info = new LeapDeviceInfo(LeapDeviceType.Peripheral);
        // TODO: DeviceList does not tell us the device type. Dragonfly serial starts with "LE" and peripheral starts with "LP"
        if (devices[0].SerialNumber.Length >= 2) {
          switch (devices[0].SerialNumber.Substring(0, 2)) {
            case ("LP"):
              info = new LeapDeviceInfo(LeapDeviceType.Peripheral);
              break;
            case ("LE"):
              info = new LeapDeviceInfo(LeapDeviceType.Dragonfly);
              break;
            default:
              break;
          }
        }

        // TODO: Add baseline & offset when included in API
        // NOTE: Alternative is to use device type since all parameters are invariant
        info.isEmbedded = devices[0].Type != Device.DeviceType.TYPE_PERIPHERAL;
        info.horizontalViewAngle = devices[0].HorizontalViewAngle * Mathf.Rad2Deg;
        info.verticalViewAngle = devices[0].VerticalViewAngle * Mathf.Rad2Deg;
        info.trackingRange = devices[0].Range / 1000f;
        info.serialID = devices[0].SerialNumber;
        return info;
      } else if (devices.Count > 1) {
        return new LeapDeviceInfo(LeapDeviceType.Peripheral);
      }
      return new LeapDeviceInfo(LeapDeviceType.Invalid);
    }

    public void ReTransformFrames() {
      transformFrame(_untransformedUpdateFrame, _transformedUpdateFrame);
      transformFrame(_untransformedFixedFrame, _transformedFixedFrame);
    }

    protected virtual void Awake() {
      _fixedOffset.delay = 0.4f;
      _smoothedTrackingLatency.SetBlend(0.99f, 0.0111f);
    }

    protected virtual void Start() {
      createController();
      _transformedUpdateFrame = new Frame();
      _transformedFixedFrame = new Frame();
      _transformedPreCullFrame = new Frame();
      _untransformedUpdateFrame = new Frame();
      _untransformedFixedFrame = new Frame();
      _untransformedPreCullFrame = new Frame();
    }

    protected virtual void Update() {
#if UNITY_EDITOR
      if (EditorApplication.isCompiling) {
        EditorApplication.isPlaying = false;
        Debug.LogWarning("Unity hot reloading not currently supported. Stopping Editor Playback.");
        return;
      }
#endif

      _fixedOffset.Update(Time.time - Time.fixedTime, Time.deltaTime);

      if (_useInterpolation) {
#if UNITY_ANDROID
        Int64 time = leap_controller_.Now() - (_interpolationDelay+16) * 1000;
        leap_controller_.GetInterpolatedFrame(_untransformedUpdateFrame, time);
#else
        _smoothedTrackingLatency.value = Mathf.Min(_smoothedTrackingLatency.value, 25000f);
        _smoothedTrackingLatency.Update((float)(leap_controller_.Now() - leap_controller_.FrameTimestamp()), Time.deltaTime);
        leap_controller_.GetInterpolatedFrame(_untransformedUpdateFrame, leap_controller_.Now() - (long)_smoothedTrackingLatency.value - (_interpolationDelay * 1000));
#endif
      } else {
        leap_controller_.Frame(_untransformedUpdateFrame);
      }

      if (_untransformedUpdateFrame != null) {
        transformFrame(_untransformedUpdateFrame, _transformedUpdateFrame);

        DispatchUpdateFrameEvent(_transformedUpdateFrame);
      }
      manualUpdateHasBeenCalledSinceUpdate = false;
    }

    protected virtual void FixedUpdate() {
      if (_reuseFramesForPhysics) {
        DispatchFixedFrameEvent(_transformedUpdateFrame);
        return;
      }

      if (_useInterpolation) {
#if UNITY_ANDROID
        Int64 time = leap_controller_.Now() - (_interpolationDelay+16) * 1000;
        leap_controller_.GetInterpolatedFrame(_untransformedFixedFrame, time);
#else
        leap_controller_.GetInterpolatedFrame(_untransformedFixedFrame, leap_controller_.Now() - (long)_smoothedTrackingLatency.value - (_interpolationDelay * 1000));
#endif
      } else {
        leap_controller_.Frame(_untransformedFixedFrame);
      }

      if (_untransformedFixedFrame != null) {
        transformFrame(_untransformedFixedFrame, _transformedFixedFrame);

        DispatchFixedFrameEvent(_transformedFixedFrame);
      }
    }

    public void ManuallyUpdateFrame(long temporalOffset = 0) {
      if (_useInterpolation) {
        leap_controller_.GetInterpolatedFrame(_untransformedPreCullFrame, leap_controller_.Now() - (long)_smoothedTrackingLatency.value - ((_interpolationDelay + temporalOffset) * 1000));
      } else {
        leap_controller_.Frame(_untransformedPreCullFrame);
      }

      if (_untransformedPreCullFrame != null) {
        transformFrame(_untransformedPreCullFrame, _transformedPreCullFrame, false);
      }
      manualUpdateHasBeenCalledSinceUpdate = true;
    }

    protected virtual void OnDestroy() {
      destroyController();
    }

    protected virtual void OnApplicationPause(bool isPaused) {
      if (leap_controller_ != null) {
        if (isPaused) {
          leap_controller_.StopConnection();
        } else {
          leap_controller_.StartConnection();
        }
      }
    }

    protected virtual void OnApplicationQuit() {
      destroyController();
    }

    /*
     * Initializes the Leap Motion policy flags.
     * The POLICY_OPTIMIZE_HMD flag improves tracking for head-mounted devices.
     */
    protected void initializeFlags() {
      if (leap_controller_ == null) {
        return;
      }
      //Optimize for top-down tracking if on head mounted display.
      if (_isHeadMounted) {
        leap_controller_.SetPolicy(Controller.PolicyFlag.POLICY_OPTIMIZE_HMD);
      } else {
        leap_controller_.ClearPolicy(Controller.PolicyFlag.POLICY_OPTIMIZE_HMD);
      }
    }
    /** Create an instance of a Controller, initialize its policy flags
     * and subscribe to connection event */
    protected void createController() {
      if (leap_controller_ != null) {
        destroyController();
      }

      leap_controller_ = new Controller();
      if (leap_controller_.IsConnected) {
        initializeFlags();
      } else {
        leap_controller_.Device += onHandControllerConnect;
      }
    }

    /** Calling this method stop the connection for the existing instance of a Controller, 
     * clears old policy flags and resets to null */
    protected void destroyController() {
      if (leap_controller_ != null) {
        if (leap_controller_.IsConnected) {
          leap_controller_.ClearPolicy(Controller.PolicyFlag.POLICY_OPTIMIZE_HMD);
        }
        leap_controller_.StopConnection();
        leap_controller_ = null;
      }
    }

    protected void onHandControllerConnect(object sender, LeapEventArgs args) {
      initializeFlags();
      leap_controller_.Device -= onHandControllerConnect;
    }

    protected void transformFrame(Frame source, Frame dest, bool resampleTemporalWarping = true) {
      LeapTransform leapTransform;
      if (_temporalWarping != null) {
        if (resampleTemporalWarping) {
          _temporalWarping.TryGetWarpedTransform(LeapVRTemporalWarping.WarpedAnchor.CENTER, out warpedPosition, out warpedRotation, source.Timestamp);
          warpedRotation = warpedRotation * transform.localRotation;
        }

        leapTransform = new LeapTransform(warpedPosition.ToVector(), warpedRotation.ToLeapQuaternion(), transform.lossyScale.ToVector() * 1e-3f);
        leapTransform.MirrorZ();
      } else {
        leapTransform = transform.GetLeapMatrix();
      }

      dest.CopyFrom(source).Transform(leapTransform);
    }

    //Late-Latching Functions
    protected virtual void OnEnable() {
      Camera.onPreCull -= LateUpdateHandTransforms;
      Camera.onPreCull += LateUpdateHandTransforms;
      _transformArray = new Matrix4x4[6];
      Shader.SetGlobalMatrixArray("_handTransforms", _transformArray);
    }

    protected virtual void OnDisable() {
      Camera.onPreCull -= LateUpdateHandTransforms;
      _transformArray = new Matrix4x4[6];
      Shader.SetGlobalMatrixArray("_handTransforms", _transformArray);
    }

    public void LateUpdateHandTransforms(Camera camera) {
      if (RealtimeGraph.Instance != null) { RealtimeGraph.Instance.BeginSample("Vertex Offset", RealtimeGraph.GraphUnits.Miliseconds); }

#if UNITY_EDITOR
      //Hard-coded name of the camera used to generate the pre-render view
      if (camera.gameObject.name == "PreRenderCamera") {
        return;
      }

      bool isScenePreviewCamera = camera.gameObject.hideFlags == HideFlags.HideAndDontSave;
      if (isScenePreviewCamera) {
        return;
      }
#endif

      if (Application.isPlaying) {
        if (!manualUpdateHasBeenCalledSinceUpdate) {
          //Add back the latency we gain by late latch to match the game latency (but increase smoothness)
          long interpolationAmount = 160;// (long)((Time.smoothDeltaTime / Time.timeScale) * 1000f);
          ManuallyUpdateFrame(interpolationAmount);
        }

        if (_transformedPreCullFrame != null) {
          for (int i = 0; i < _transformedPreCullFrame.Hands.Count; i++) {
            Hand preCullHand = _transformedPreCullFrame.Hands[i];
            Hand updateHand = _transformedUpdateFrame.Hand(preCullHand.Id);

            if (preCullHand != null && updateHand != null) {
              if (!_transformedPreCullFrame.Hands[i].IsLeft) {
                _transformArray[0] = Matrix4x4.TRS(preCullHand.PalmPosition.ToVector3() - updateHand.PalmPosition.ToVector3(), Quaternion.identity, Vector3.one); //Step Three: Offset the model in worldspace
                _transformArray[1] = Matrix4x4.TRS(new Vector3(0.05f, 0f, 0f), Quaternion.Euler(0f, 90f, 0f) * (Quaternion.Inverse(updateHand.Rotation.ToQuaternion()) * preCullHand.Rotation.ToQuaternion()), Vector3.one); //Step Two: Rotate the model around the palm and translate back to wrist.
                _transformArray[2] = Matrix4x4.TRS(new Vector3(0f, 0f, -0.05f), Quaternion.Euler(0f, -90f, 0f), Vector3.one); //Step One: Translate wrist to palm (rotation pivot) 
              } else {
                _transformArray[3] = Matrix4x4.TRS(preCullHand.PalmPosition.ToVector3() - updateHand.PalmPosition.ToVector3(), Quaternion.identity, Vector3.one); //Step Three: Offset the model in worldspace
                _transformArray[4] = Matrix4x4.TRS(new Vector3(-0.05f, 0f, 0f), Quaternion.Euler(0f,90f,0f)*(Quaternion.Inverse(updateHand.Rotation.ToQuaternion()) * preCullHand.Rotation.ToQuaternion()), Vector3.one); //Step Two: Rotate the model around the palm and translate back to wrist.
                _transformArray[5] = Matrix4x4.TRS(new Vector3(0f, 0f, 0.05f), Quaternion.Euler(0f, -90f, 0f), Vector3.one); //Step One: Translate wrist to palm (rotation pivot) 
              }
            }
          }
        }

        Shader.SetGlobalMatrixArray("_handTransforms", _transformArray);

      }
      if (RealtimeGraph.Instance != null) { RealtimeGraph.Instance.EndSample(); }
    }
  }
}
