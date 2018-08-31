using UnityEngine;

public class OvrAvatarTouchController : MonoBehaviour {
  public Animator animator;

  public void UpdatePose(OvrAvatarDriver.ControllerPose p) {
    if (!gameObject.activeInHierarchy || animator == null) return;

    animator.SetFloat("Button 1", p.button1IsDown ? 1.0f : 0.0f);
    animator.SetFloat("Button 2", p.button2IsDown ? 1.0f : 0.0f);
    animator.SetFloat("Joy X", p.joystickPosition.x);
    animator.SetFloat("Joy Y", p.joystickPosition.y);
    animator.SetFloat("Trigger", p.indexTrigger);
    animator.SetFloat("Grip", p.gripTrigger);
  }
}