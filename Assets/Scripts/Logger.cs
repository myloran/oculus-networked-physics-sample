/**
 * Copyright (c) 2017-present, Facebook, Inc.
 * All rights reserved.
 *
 * This source code is licensed under the BSD-style license found in the
 * LICENSE file in the Scripts directory of this source tree. An additional grant 
 * of patent rights can be found in the PATENTS file in the same directory.
 */
using UnityEngine;
using System.Collections;

public class Logger : MonoBehaviour {
  Queue queue = new Queue();
  string log;

  void OnEnable() {
    Application.logMessageReceived += HandleLog;
  }

  void OnDisable() {
    Application.logMessageReceived -= HandleLog;
  }

  void HandleLog(string message, string stackTrace, LogType type) {
    queue.Enqueue("\n [" + type + "] : " + message);

    if (type == LogType.Exception)
      queue.Enqueue("\n" + stackTrace);

    while (queue.Count > 30)
      queue.Dequeue();

    log = string.Empty;

    foreach (string s in queue)
      log += s;
  }

  void OnGUI() => GUI.TextArea(new Rect(0, 0, Screen.width / 3, Screen.height), log);
}