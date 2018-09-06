/**
 * Copyright (c) 2017-present, Facebook, Inc.
 * All rights reserved.
 *
 * This source code is licensed under the BSD-style license found in the
 * LICENSE file in the Scripts directory of this source tree. An additional grant 
 * of patent rights can be found in the PATENTS file in the same directory.
 */
using UnityEngine;
using UnityEngine.Assertions;
using static AvatarState;

public class RemoteAvatar : OvrAvatarDriver {
  public class Hand {
    public Animator animator;
    public Transform transform;

    public GameObject
      point,
      grip;
  }

  Context context;
  Pose pose = new Pose();

  Hand
    leftHand = new Hand(),
    rightHand = new Hand();
  
  const float LineWidth = 0.25f;
  int clientId;

  public void SetClientId(int id) => clientId = id;
  public void SetContext(Context c) => context = c;
  public void CubeAttached(ref Hand h) => CreatePoint(ref h);
  public Hand GetLeftHand() => leftHand;
  public Hand GetRightHand() => rightHand;
  public GameObject GetHead() => GetComponent<OvrAvatar>().Head.gameObject;

  void Start() {
    var a = (OvrAvatar)GetComponent(typeof(OvrAvatar));
    leftHand.animator = a.HandLeft.animator;
    rightHand.animator = a.HandRight.animator;
    leftHand.transform = a.HandLeftRoot;
    rightHand.transform = a.HandRightRoot;
    Assert.IsNotNull(leftHand.transform);
    Assert.IsNotNull(rightHand.transform);
  }

  void CreatePoint(ref Hand h) {
    if (h.point) return;

    h.point = Instantiate(context.remoteLinePrefabs[clientId], Vector3.zero, Quaternion.identity);
    Assert.IsNotNull(h.point);
    UpdatePoint(ref h);
  }

  void UpdatePoint(ref Hand h) {
    if (!h.point) return;

    var line = h.point.GetComponent<LineRenderer>();
    if (!line) return;

    var start = h.transform.position;
    var finish = h.grip.transform.position;

    if ((finish - start).magnitude < 1) {
      line.positionCount = 0;
      return;
    }

    line.positionCount = 2;
    line.SetPosition(0, start);
    line.SetPosition(1, finish);
    line.startWidth = LineWidth;
    line.endWidth = LineWidth;
  }

  public void DetachCube(ref Hand h) {
    if (!h.grip) return;

    Destroy(h.point);
    h.point = null;
    var rigidBody = h.grip.GetComponent<Rigidbody>();
    rigidBody.isKinematic = false;
    rigidBody.detectCollisions = true;
    h.grip.transform.SetParent(null);
    h.grip = null;
  }

  public void Update() {
    UpdateHand(ref leftHand);
    UpdateHand(ref rightHand);
    UpdatePoint(ref leftHand);
    UpdatePoint(ref rightHand);
  }

  public void UpdateHand(ref Hand h) {
    if (!h.grip) return;

    var network = h.grip.GetComponent<NetworkCube>(); //while an object is held, set its last interaction frame to the current sim frame. this is used to boost priority for the object when it is thrown.
    network.heldFrame = (long)context.simulationFrame;
  }

  public bool GetAvatarState(out AvatarState s) {
    Initialize(out s, clientId, pose, leftHand.grip, rightHand.grip);
    return true;
  }

  public void ApplyAvatarPose(ref AvatarState s) 
    => UpdatePose(ref s, clientId, pose, context);

  public void ApplyLeftHandUpdate(ref AvatarState s) 
    => UpdateLeftHand(ref s, clientId, context, this);

  public void ApplyRightHandUpdate(ref AvatarState s) 
    => UpdateRightHand(ref s, clientId, context, this);

  public override bool GetPose(out Pose p) {
    p = pose;
    return true;
  }

}