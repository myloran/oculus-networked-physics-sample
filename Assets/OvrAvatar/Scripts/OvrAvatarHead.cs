using UnityEngine;
using System.Collections;
using System.Collections.Generic;

public class OvrAvatarHead : MonoBehaviour {
  List<Material> voiceMaterials = new List<Material>();

  void Start() {
    foreach (var renderer in GetComponentsInChildren<Renderer>()) {
      foreach (var m in renderer.materials) {
        if (m.HasProperty("_VoiceAmplitude"))
          voiceMaterials.Add(m);
      }
    }
  }

  public void UpdatePose(float voiceAmplitude) {
    if (!gameObject.activeInHierarchy) return;

    foreach (var m in voiceMaterials) 
      m.SetFloat("_VoiceAmplitude", voiceAmplitude);
  }
}