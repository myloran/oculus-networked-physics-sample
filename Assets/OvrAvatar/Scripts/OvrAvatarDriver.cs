using UnityEngine;

public abstract class OvrAvatarDriver : MonoBehaviour {
  public struct ControllerPose {
    public bool button1IsDown;
    public bool button2IsDown;
    public Vector2 joystickPosition;
    public float indexTrigger;
    public float gripTrigger;

    public static ControllerPose Interpolate(ControllerPose from, ControllerPose to, float time) {
      return new ControllerPose {
        button1IsDown = time < 0.5f ? from.button1IsDown : to.button1IsDown,
        button2IsDown = time < 0.5f ? from.button2IsDown : to.button2IsDown,
        joystickPosition = Vector2.Lerp(from.joystickPosition, to.joystickPosition, time),
        indexTrigger = Mathf.Lerp(from.indexTrigger, to.indexTrigger, time),
        gripTrigger = Mathf.Lerp(from.gripTrigger, to.gripTrigger, time),
      };
    }
  }

  public struct HandPose {
    public float indexFlex;
    public float gripFlex;
    public bool isPointing;
    public bool isThumbUp;

    public static HandPose Interpolate(HandPose from, HandPose to, float time) {
      return new HandPose {
        indexFlex = Mathf.Lerp(from.indexFlex, to.indexFlex, time),
        gripFlex = Mathf.Lerp(from.gripFlex, to.gripFlex, time),
        isPointing = time < 0.5f ? from.isPointing : to.isPointing,
        isThumbUp = time < 0.5f ? from.isThumbUp : to.isThumbUp,
      };
    }
  }

  public class Pose {
    public Vector3 
      headPosition,
      handLeftPosition,
      handRightPosition;

    public Quaternion 
      headRotation,
      handLeftRotation,
      handRightRotation;

    public ControllerPose 
      controllerLeftPose,
      controllerRightPose;

    public HandPose 
      handLeftPose,
      handRightPose;

    public float voiceAmplitude;

    public static Pose Interpolate(Pose from, Pose to, float time) {
      return new Pose {
        headPosition = Vector3.Lerp(from.headPosition, to.headPosition, time),
        headRotation = Quaternion.Slerp(from.headRotation, to.headRotation, time),
        handLeftPosition = Vector3.Lerp(from.handLeftPosition, to.handLeftPosition, time),
        handLeftRotation = Quaternion.Slerp(from.handLeftRotation, to.handLeftRotation, time),
        handRightPosition = Vector3.Lerp(from.handRightPosition, to.handRightPosition, time),
        handRightRotation = Quaternion.Slerp(from.handRightRotation, to.handRightRotation, time),
        voiceAmplitude = Mathf.Lerp(from.voiceAmplitude, to.voiceAmplitude, time),
        controllerLeftPose = ControllerPose.Interpolate(from.controllerLeftPose, to.controllerLeftPose, time),
        controllerRightPose = ControllerPose.Interpolate(from.controllerRightPose, to.controllerRightPose, time),
        handLeftPose = HandPose.Interpolate(from.handLeftPose, to.handLeftPose, time),
        handRightPose = HandPose.Interpolate(from.handRightPose, to.handRightPose, time),
      };
    }
  };

  public abstract bool GetPose(out Pose pose);
}