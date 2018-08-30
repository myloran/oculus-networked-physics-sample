using UnityEngine;
using System;

public class OvrAvatarLocalDriver : OvrAvatarDriver {
  const int VoiceFrequency = 48000;
  const float VoiceEmaAlpha = 0.0005f;
  float emaAlpha = VoiceEmaAlpha;
  float voiceAmplitude = 0.0f;

  ControllerPose GetControllerPose(OVRInput.Controller c) {
    return new ControllerPose {
      button1IsDown = OVRInput.Get(OVRInput.Button.One, c),
      button2IsDown = OVRInput.Get(OVRInput.Button.Two, c),
      joystickPosition = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, c),
      indexTrigger = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, c),
      gripTrigger = OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, c),
    };
  }

  HandPose GetHandPose(OVRInput.Controller c) {
    return new HandPose {
      indexFlex = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, c),
      gripFlex = OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, c),
      isPointing = !OVRInput.Get(OVRInput.NearTouch.PrimaryIndexTrigger, c),
      isThumbUp = !OVRInput.Get(OVRInput.NearTouch.PrimaryThumbButtons, c),
    };
  }

  void Start() {
    var audio = GetComponent<AudioSource>();
    if (audio == null) return;

    string selectedDevice = null;
    int frequency = VoiceFrequency;

    foreach (string device in Microphone.devices) {
      if (device == "Microphone (Rift Audio)") {
        selectedDevice = device;
        int min;
        int max;
        Microphone.GetDeviceCaps(device, out min, out max);
        frequency = max != 0 ? max : VoiceFrequency;
        emaAlpha *= VoiceFrequency / (float)frequency;
        break;
      }
    }
    audio.clip = Microphone.Start(selectedDevice, true, 1, frequency);
    audio.loop = true;
    audio.Play();
  }

  void OnAudioFilterRead(float[] data, int channels) {
    for (int i = 0; i < data.Length; i += channels) {
      voiceAmplitude = Math.Abs(data[i]) * emaAlpha + voiceAmplitude * (1 - emaAlpha);
      data[i] = 0;
      data[i + 1] = 0;
    }
  }

  public override bool GetCurrentPose(out PoseFrame p) {
    p = new PoseFrame {
      voiceAmplitude = voiceAmplitude,
      headPosition = UnityEngine.XR.InputTracking.GetLocalPosition(UnityEngine.XR.XRNode.CenterEye),
      headRotation = UnityEngine.XR.InputTracking.GetLocalRotation(UnityEngine.XR.XRNode.CenterEye),
      handLeftPosition = OVRInput.GetLocalControllerPosition(OVRInput.Controller.LTouch),
      handLeftRotation = OVRInput.GetLocalControllerRotation(OVRInput.Controller.LTouch),
      handRightPosition = OVRInput.GetLocalControllerPosition(OVRInput.Controller.RTouch),
      handRightRotation = OVRInput.GetLocalControllerRotation(OVRInput.Controller.RTouch),
      controllerLeftPose = GetControllerPose(OVRInput.Controller.LTouch),
      handLeftPose = GetHandPose(OVRInput.Controller.LTouch),
      controllerRightPose = GetControllerPose(OVRInput.Controller.RTouch),
      handRightPose = GetHandPose(OVRInput.Controller.RTouch),
    };
    return true;
  }
}