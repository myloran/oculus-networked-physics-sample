using UnityEngine;
using System;
using UnityEngine.XR;
using static OVRInput;

public class OvrAvatarLocalDriver : OvrAvatarDriver {
  const int VoiceFrequency = 48000;
  const float VoiceEmaAlpha = 0.0005f;
  float emaAlpha = VoiceEmaAlpha;
  float voiceAmplitude = 0.0f;

  ControllerPose GetControllerPose(Controller c)
    => new ControllerPose {
      button1IsDown = Get(Button.One, c),
      button2IsDown = Get(Button.Two, c),
      joystickPosition = Get(Axis2D.PrimaryThumbstick, c),
      indexTrigger = Get(Axis1D.PrimaryIndexTrigger, c),
      gripTrigger = Get(Axis1D.PrimaryHandTrigger, c),
    };

  HandPose GetHandPose(Controller c)
    => new HandPose {
      indexFlex = Get(Axis1D.PrimaryIndexTrigger, c),
      gripFlex = Get(Axis1D.PrimaryHandTrigger, c),
      isPointing = !Get(NearTouch.PrimaryIndexTrigger, c),
      isThumbUp = !Get(NearTouch.PrimaryThumbButtons, c),
    };

  void Start() {
    var audio = GetComponent<AudioSource>();
    if (audio == null) return;

    string device = null;
    int frequency = VoiceFrequency;

    foreach (var d in Microphone.devices) {
      if (d == "Microphone (Rift Audio)") {
        device = d;
        int min;
        int max;
        Microphone.GetDeviceCaps(d, out min, out max);
        frequency = max != 0 ? max : VoiceFrequency;
        emaAlpha *= VoiceFrequency / (float)frequency;
        break;
      }
    }
    audio.clip = Microphone.Start(device, true, 1, frequency);
    audio.loop = true;
    audio.Play();
  }

  void OnAudioFilterRead(float[] data, int channels) {
    for (int i = 0; i < data.Length; i += channels) {
      voiceAmplitude = Math.Abs(data[i]) * emaAlpha
        + voiceAmplitude * (1 - emaAlpha);

      data[i] = 0;
      data[i + 1] = 0;
    }
  }

  public override bool GetPose(out Pose p) {
    p = new Pose {
      voiceAmplitude = voiceAmplitude,
      headPosition = InputTracking.GetLocalPosition(XRNode.CenterEye),
      headRotation = InputTracking.GetLocalRotation(XRNode.CenterEye),
      handLeftPosition = GetLocalControllerPosition(Controller.LTouch),
      handLeftRotation = GetLocalControllerRotation(Controller.LTouch),
      handRightPosition = GetLocalControllerPosition(Controller.RTouch),
      handRightRotation = GetLocalControllerRotation(Controller.RTouch),
      controllerLeftPose = GetControllerPose(Controller.LTouch),
      handLeftPose = GetHandPose(Controller.LTouch),
      controllerRightPose = GetControllerPose(Controller.RTouch),
      handRightPose = GetHandPose(Controller.RTouch),
    };
    return true;
  }
}