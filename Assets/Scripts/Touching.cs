/**
 * Copyright (c) 2017-present, Facebook, Inc.
 * All rights reserved.
 *
 * This source code is licensed under the BSD-style license found in the
 * LICENSE file in the Scripts directory of this source tree. An additional grant 
 * of patent rights can be found in the PATENTS file in the same directory.
 */
using UnityEngine;

public class Touching : MonoBehaviour {
  public Context context;
  public int cubeId;

  public void Initialize(Context context, int cubeId) {
    this.context = context;
    this.cubeId = cubeId;
  }

  void OnTriggerEnter(Collider other) {
    var t = other.gameObject.GetComponent<Touching>();
    if (!t) return;

    context.StartTouching(cubeId, t.cubeId);
  }

  void OnTriggerExit(Collider other) {
    var t = other.gameObject.GetComponent<Touching>();
    if (!t) return;

    context.FinishTouching(cubeId, t.cubeId);
  }
}