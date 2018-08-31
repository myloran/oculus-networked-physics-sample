using UnityEngine;

public class OvrAvatarHand : MonoBehaviour {
  public Animator animator;
  bool showControllers = false;

  public void HoldController(bool show) => showControllers = show;

  public void UpdatePose(OvrAvatarDriver.HandPose p) {
    if (!gameObject.activeInHierarchy || animator == null) return;

    animator.SetBool("HoldController", showControllers);
    animator.SetFloat("Flex", p.gripFlex);
    animator.SetFloat("Pinch", p.indexFlex);
    animator.SetLayerWeight(animator.GetLayerIndex("Point Layer"), p.isPointing ? 1.0f : 0.0f);
    animator.SetLayerWeight(animator.GetLayerIndex("Thumb Layer"), p.isThumbUp ? 1.0f : 0.0f);
  }
}