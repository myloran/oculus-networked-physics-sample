/**
 * Copyright (c) 2017-present, Facebook, Inc.
 * All rights reserved.
 *
 * This source code is licensed under the BSD-style license found in the
 * LICENSE file in the Scripts directory of this source tree. An additional grant 
 * of patent rights can be found in the PATENTS file in the same directory.
 */

using System;
using UnityEngine;
using UnityEngine.Assertions;
using System.Collections.Generic;

public class Avatar : OvrAvatarLocalDriver {
  public struct HandInput {
    public float handTrigger;
    public float indexTrigger;
    public float previousIndexTrigger;
    public ulong indexPressFrame;
    public bool pointing;
    public bool x;
    public bool y;
    public Vector2 stick;
  }

  public enum HandState {
    Neutral,
    Pointing,
    Grip,
  }

  public class HandData {
    public struct ThrowRingBufferEntry {
      public bool valid;
      public float speed;
    };

    public List<GameObject> supports;
    public Animator animator;
    public Transform transform;

    public GameObject line,
      point,
      grip;

    public ThrowRingBufferEntry[] throws = new ThrowRingBufferEntry[ThrowRingBufferSize];
    public HandInput input;
    public HandState state = HandState.Neutral;

    public Vector3 
      prevHandPosition,
      prevGripPosition;

    public Quaternion 
      prevHandRotation,
      prevGripRotation;

    public string name;

    public int 
      id,
      throwIndex;

    public bool 
      isTouching,
      hasReleaseVelocity;

    public ulong 
      pointFrame,
      inputFrame,
      startFrame,
      releaseFrame;
  };

  public const int LeftHand = 0;
  public const int RightHand = 1;
  const float LineWidth = 0.02f;
  const float RaycastDistance = 256.0f;
  const ulong PointDebounceFrames = 10;
  const ulong PointStickyFrames = 45;
  const float PointSphereCastStart = 10.0f;
  const float PointSphereCastRadius = 0.25f;
  const float MinimumCubeHeight = 0.1f;
  const float IndexThreshold = 0.5f;
  const float IndexStickyFrames = 45;
  const float GripThreshold = 0.75f;
  const float GrabDistance = 1.0f;
  const float GrabRadius = 0.05f;
  const float WarpDistance = GrabDistance * 0.5f;
  const float ZoomMinimum = 0.4f;
  const float ZoomMaximum = 20.0f;
  const float ZoomSpeed = 4.0f;
  const float RotateSpeed = 180.0f;
  const float StickThreshold = 0.65f;
  const int PostReleaseDisableSelectFrames = 20;
  const int ThrowRingBufferSize = 16;
  const float ThrowSpeed = 5.0f;
  const float HardThrowSpeed = 10.0f;
  const float MaxThrowSpeed = 20.0f;
  const float ThrowVelocityMinY = 2.5f;
  public GameObject linePrefab;
  OvrAvatar avatar;
  Context context;
  HandData leftHand = new HandData();
  HandData rightHand = new HandData();
  int contextLayerCubes;
  int contextLayerGrip;
  int contextLayerMask;

  public void SetContext(Context c) {
    Assert.IsNotNull(c);
    context = c;
    ResetHand(ref leftHand);
    ResetHand(ref rightHand);
    contextLayerCubes = c.gameObject.layer;
    contextLayerGrip = c.gameObject.layer + 1;
    contextLayerMask = (1 << contextLayerCubes) | (1 << contextLayerGrip);
  }

  void Start() {
    Assert.IsNotNull(linePrefab);
    avatar = (OvrAvatar)GetComponent(typeof(OvrAvatar));
    leftHand.id = LeftHand;
    rightHand.id = RightHand;
    leftHand.name = "left hand";
    rightHand.name = "right hand";
    leftHand.animator = avatar.HandLeft.animator;
    rightHand.animator = avatar.HandRight.animator;
    leftHand.transform = avatar.HandLeftRoot;
    rightHand.transform = avatar.HandRightRoot;
    Assert.IsNotNull(leftHand.transform);
    Assert.IsNotNull(rightHand.transform);
  }

  void Update() {
    Assert.IsNotNull(context);
    PoseFrame frame;
    if (!avatar.Driver.GetCurrentPose(out frame)) return;

    UpdateHand(ref leftHand, frame);
    UpdateHand(ref rightHand, frame);
  }

  void FixedUpdate() {
    Assert.IsNotNull(context);
    PoseFrame frame;
    if (!avatar.Driver.GetCurrentPose(out frame)) return;

    UpdateHandFixed(ref leftHand, frame);
    UpdateHandFixed(ref rightHand, frame);
  }

  void UpdateHand(ref HandData h, PoseFrame frame) {
    var hand = (h.id == LeftHand) ? frame.handLeftPose : frame.handRightPose;
    var controller = (h.id == LeftHand) ? frame.controllerLeftPose : frame.controllerRightPose;

    TranslateHandPoseToInput(ref hand, ref controller, ref h.input);
    UpdateRotate(ref h);
    UpdateZoom(ref h);
    UpdateGrip(ref h);
    DetectStateChanges(ref h);
    UpdateCurrentState(ref h);
    UpdateHeldObject(ref h);
  }

  void UpdateHandFixed(ref HandData h, PoseFrame frame) => UpdateSnapToHand(ref h);

  void UpdateRotate(ref HandData h) {
    if (!h.grip) return;

    var angle = 0.0f;

    if (h.input.stick.x <= -StickThreshold)
      angle = +RotateSpeed * Time.deltaTime;

    if (h.input.stick.x >= +StickThreshold)
      angle = -RotateSpeed * Time.deltaTime;

    h.grip.transform.RotateAround(
      h.grip.transform.position,
      h.transform.forward, 
      angle);
  }

  void UpdateZoom(ref HandData h) {
    if (!h.grip) return;

    Vector3 start, direction;
    GetIndexFingerStartPointAndDirection(ref h, out start, out direction);
    var position = h.grip.transform.position;

    if (h.input.stick.y <= -StickThreshold) {
      var delta = position - start; //zoom in: sneaky trick, pull center of mass towards hand on zoom in!
      var distance = delta.magnitude;

      if (distance > ZoomMinimum) {
        distance -= ZoomSpeed * Time.deltaTime;

        if (distance < ZoomMinimum)
          distance = ZoomMinimum;

        h.grip.transform.position = start + delta;
      }
    }

    if (h.input.stick.y >= +StickThreshold) {      
      if (Vector3.Dot(position - start, direction) < ZoomMaximum) //zoom out: push out strictly along point direction. this lets objects grabbed up close always zoom out in a consistent direction
        h.grip.transform.position += (ZoomSpeed * Time.deltaTime) * direction;
    }
  }

  void UpdateSnapToHand(ref HandData h) {
    if (!h.grip) return;

    Vector3 start, direction;
    GetIndexFingerStartPointAndDirection(ref h, out start, out direction);

    if (h.input.indexTrigger < IndexThreshold
      || h.input.indexPressFrame + IndexStickyFrames < context.GetRenderFrame()
    ) return;

    h.input.indexPressFrame = 0;
    var delta = h.grip.transform.position - start; //warp to hand on index grip
    var distance = delta.magnitude;

    if (distance > WarpDistance) {
      distance = WarpDistance;

      if (distance < ZoomMinimum)
        distance = ZoomMinimum;

      var rigidBody = h.grip.GetComponent<Rigidbody>();
      var network = h.grip.GetComponent<NetworkInfo>();
      network.SmoothMove(start + delta, rigidBody.rotation);
    }

    for (int i = 0; i < ThrowRingBufferSize; ++i) { //clear the throw ring buffer
      h.throws[i].valid = false;
      h.throws[i].speed = 0.0f;
    }
  }

  void UpdateGrip(ref HandData h) {
    if (h.input.handTrigger <= GripThreshold)
      h.inputFrame = 0;

    else if (h.inputFrame == 0)
      h.inputFrame = context.GetRenderFrame();
  }

  void UpdateHeldObject(ref HandData h) {
    if (!h.grip) return;

    int index = (h.throwIndex++) % ThrowRingBufferSize; //track data to improve throw release in ring buffer
    var diff = h.grip.transform.position - h.prevGripPosition;
    var speed = (float)Math.Sqrt(diff.x * diff.x + diff.z * diff.z);
    h.throws[index].valid = true;
    h.throws[index].speed = speed * Constants.RenderFrameRate;

    //track previous positions and rotations for hand and index finger so we can use this to determine linear and angular velocity at time of release
    h.prevHandPosition = h.transform.position;
    h.prevHandRotation = h.transform.rotation;

    h.prevGripPosition = h.grip.transform.position;
    h.prevGripRotation = h.grip.transform.rotation;

    //while an object is held set its last interaction frame to the current sim frame. this is used to boost priority for this object when it is thrown.
    var network = h.grip.GetComponent<NetworkInfo>();
    network.SetLastFrame((long)context.GetSimulationFrame());
  }

  void TranslateHandPoseToInput(ref HandPose handPose, ref ControllerPose controllerPose, ref HandInput i) {
    i.handTrigger = handPose.gripFlex;
    i.previousIndexTrigger = i.indexTrigger;
    i.indexTrigger = handPose.indexFlex;

    if (i.indexTrigger >= IndexThreshold && i.previousIndexTrigger < IndexThreshold)
      i.indexPressFrame = context.GetRenderFrame();

    i.pointing = true;
    i.x = controllerPose.button1IsDown;
    i.y = controllerPose.button2IsDown;
    i.stick = controllerPose.joystickPosition;
  }

  void DetectStateChanges(ref HandData h) {
    if (h.state == HandState.Neutral) {
      if (DetectGripTransition(ref h)) return;

      if (h.input.pointing) {
        TransitionToState(ref h, HandState.Pointing);
        return;
      }

    } else if(h.state == HandState.Pointing) {
      if (DetectGripTransition(ref h))return;

      if (!h.input.pointing) {
        TransitionToState(ref h, HandState.Neutral);
        return;
      }

    } else if (h.state == HandState.Grip) {
      if (h.input.handTrigger >= GripThreshold) return;

      if (h.input.pointing)
        TransitionToState(ref h, HandState.Pointing);
      else
        TransitionToState(ref h, HandState.Neutral);
    }
  }

  bool DetectGripTransition(ref HandData h) {
    if (h.state == HandState.Grip && h.grip == null) {
      TransitionToState(ref h, HandState.Neutral);
      return true;
    }

    if (h.input.handTrigger < GripThreshold) return false;
    if (h.point || h.pointFrame + PointStickyFrames < context.GetRenderFrame()) return false;

    var network = h.point.GetComponent<NetworkInfo>();
    if (!network.CanGrabCube(h.inputFrame)) return false;

    AttachToHand(ref h, h.point);
    TransitionToState(ref h, HandState.Grip);

    return true;
  }

  void AttachToHand(ref HandData h, GameObject gameObject) {
    var network = gameObject.GetComponent<NetworkInfo>();
    network.AttachCubeToLocalPlayer(this, h);

#if DEBUG_AUTHORITY
        Debug.Log( "client " + context.GetClientIndex() + " grabbed cube " + network.GetCubeId() + " and set ownership sequence to " + network.GetOwnershipSequence() );
#endif // #if DEBUG_AUTHORITY

    if (!context.IsServer())
      network.ClearConfirmed();
    else
      network.SetConfirmed();

    for (int i = 0; i < ThrowRingBufferSize; ++i) {
      h.throws[i].valid = false;
      h.throws[i].speed = 0.0f;
    }
  }

  bool IsCloseGrip(ref HandData h) {
    if (!h.grip) return false;

    var delta = h.grip.transform.position - h.transform.position;

    return delta.magnitude <= GrabDistance;
  }

  bool IsThrowing(ref HandData h) {
    int count = 0;
    var sum = 0.0f;

    for (int i = 0; i < ThrowRingBufferSize; ++i) {
      if (h.throws[i].valid)
        sum += h.throws[i].speed;

      count++;
    }
    if (count == 0) return false;

    return sum/count >= ThrowSpeed;
  }

  void Release(ref HandData hand, Rigidbody r, bool disableReleaseVelocity = false) {
    if (disableReleaseVelocity) {
      r.velocity = Vector3.zero;
      r.angularVelocity = Vector3.zero;

    } else if (IsCloseGrip(ref hand) || IsThrowing(ref hand)) {     
      r.velocity = (hand.grip.transform.position - hand.prevGripPosition) * Constants.RenderFrameRate; //throw mode
      r.angularVelocity = CalculateAngularVelocity(hand.prevGripRotation, hand.grip.transform.rotation, 1.0f / Constants.RenderFrameRate, 0.001f);
      var speed = r.velocity.magnitude;

      if (r.velocity.magnitude > MaxThrowSpeed) {
        r.velocity = (r.velocity / speed) * MaxThrowSpeed;
      }

      if (r.velocity.x * r.velocity.x + r.velocity.z * r.velocity.z > HardThrowSpeed * HardThrowSpeed) {
        if (r.velocity.y < ThrowVelocityMinY)
          r.velocity = new Vector3(r.velocity.x, ThrowVelocityMinY, r.velocity.z);
      }

    } else {        
      r.velocity = 3 * (hand.transform.position - hand.prevHandPosition) * Constants.RenderFrameRate; //placement mode
      r.angularVelocity = 2 * CalculateAngularVelocity(hand.prevHandRotation, hand.transform.rotation, 1.0f / Constants.RenderFrameRate, 0.1f);
    }
  }

  void WakeUpObjects(List<GameObject> list) {
    foreach (var obj in list) {
      var network = obj.GetComponent<NetworkInfo>();
      context.ResetBuffer(network.GetCubeId());

      if (network.GetAuthorityId() == 0)
        context.TakeAuthority(network);

      obj.GetComponent<Rigidbody>().WakeUp();
    }
  }

  void DetachFromHand(ref HandData h) {
    if (h.grip == null) return; //IMPORTANT: This happens when passing a cube from hand-to-hand

    var network = h.grip.GetComponent<NetworkInfo>();
    network.DetachCube();

#if DEBUG_AUTHORITY
        Debug.Log( "client " + context.GetClientIndex() + " released cube " + network.GetCubeId() + ". ownership sequence is " + network.GetOwnershipSequence() + ", authority sequence is " + network.GetAuthoritySequence() );
#endif // #if DEBUG_AUTHORITY
  }

  void TransitionToState(ref HandData h, HandState nextState) {
    ExitState(ref h, nextState);
    EnterState(ref h, nextState);
  }

  void ExitState(ref HandData h, HandState nextState) {
    if (h.state == HandState.Pointing)
      DestroyPointingLine(ref h);

    else if (h.state == HandState.Grip)
      DetachFromHand(ref h);
  }

  void EnterState(ref HandData h, HandState nextState) {
    if (nextState == HandState.Pointing)
      CreatePointingLine(ref h);

    h.state = nextState;
  }

  void UpdateCurrentState(ref HandData h) {
    if (h.state == HandState.Pointing) {
      UpdatePointingLine(ref h);
      ForcePointAnimation(ref h);

    } else if (h.state == HandState.Grip) {
      if (IsCloseGrip(ref h))
        ForceGripAnimation(ref h);
      else
        ForcePointAnimation(ref h);

      if (!h.grip) return;
      if (h.grip.transform.position.y > 0.0f) return;

      var position = h.grip.transform.position;
      position.y = 0.0f;
      h.grip.transform.position = position;
    }
  }

  void SetPointObject(ref HandData h, GameObject gameObject) {
    h.point = gameObject;
    h.pointFrame = context.GetRenderFrame();
  }

  void CreatePointingLine(ref HandData h) {
    if (h.line) return;

    h.line = Instantiate(linePrefab, Vector3.zero, Quaternion.identity);
    Assert.IsNotNull(h.line);
  }

  bool FilterPointObject(ref HandData hand, Rigidbody r) {
    if (!r) return false;

    var obj = r.gameObject;
    if (!obj) return false;

    if (obj.layer != contextLayerCubes && obj.layer != contextLayerGrip)
      return false;

    var network = obj.GetComponent<NetworkInfo>();
    if (!network) return false;

    return true;
  }

  void GetIndexFingerStartPointAndDirection(ref HandData h, out Vector3 start, out Vector3 direction) {
    var positions = new[] {
      new Vector3(-0.05f, 0.0f, 0.0f),
      new Vector3(+0.05f, 0.0f, 0.0f)
    };
    start = h.transform.TransformPoint(positions[h.id]);
    direction = h.transform.forward;
  }

  void UpdatePointingLine(ref HandData h) {
    Assert.IsNotNull(h.line);
    Vector3 start, direction;
    GetIndexFingerStartPointAndDirection(ref h, out start, out direction);

    var finish = start + direction * RaycastDistance;
    
    if (h.releaseFrame + PostReleaseDisableSelectFrames < context.GetRenderFrame()) { //don't allow any selection for a few frames after releasing an object      
      var hitColliders = Physics.OverlapSphere(h.transform.position, GrabRadius, contextLayerMask); //first select any object overlapping the hand

      if (hitColliders.Length > 0 && FilterPointObject(ref h, hitColliders[0].attachedRigidbody)) {
        finish = start;
        SetPointObject(ref h, hitColliders[0].gameObject);
        h.isTouching = true;
      } else {       
        h.isTouching = false; //otherwise, raycast forward along point direction for accurate selection up close
        RaycastHit hitInfo;

        if (Physics.Linecast(start, finish, out hitInfo, contextLayerMask)) {
          if (FilterPointObject(ref h, hitInfo.rigidbody)) {
            finish = start + direction * hitInfo.distance;
            SetPointObject(ref h, hitInfo.rigidbody.gameObject);
          }

        } else if (Physics.SphereCast(start + direction * PointSphereCastStart, PointSphereCastRadius, finish, out hitInfo, contextLayerMask)) {
          // failing an accurate hit, sphere cast starting from a bit further away to provide easier selection of far away objects
          if (FilterPointObject(ref h, hitInfo.rigidbody)) {
            finish = start + direction * (PointSphereCastStart + hitInfo.distance);
            SetPointObject(ref h, hitInfo.rigidbody.gameObject);
          }
        }
      }
    }

    var line = h.line.GetComponent<LineRenderer>();
    if (!line) return;

    line.positionCount = 2;
    line.SetPosition(0, start);
    line.SetPosition(1, finish);
    line.startWidth = LineWidth;
    line.endWidth = LineWidth;
  }

  void DestroyPointingLine(ref HandData h) {
    Assert.IsNotNull(h.line);
    Destroy(h.line);
    h.line = null;
  }

  void ForcePointAnimation(ref HandData h) {
    if (h.isTouching && h.state == HandState.Pointing)
      h.animator.SetLayerWeight(h.animator.GetLayerIndex("Point Layer"), 0.0f); //indicates state of touching an object to player (for up-close grip)
    else
      h.animator.SetLayerWeight(h.animator.GetLayerIndex("Point Layer"), 1.0f);

    h.animator.SetLayerWeight(h.animator.GetLayerIndex("Thumb Layer"), 0.0f);
  }

  void ForceGripAnimation(ref HandData h) {
    h.animator.SetLayerWeight(h.animator.GetLayerIndex("Point Layer"), 0.0f);
    h.animator.SetLayerWeight(h.animator.GetLayerIndex("Thumb Layer"), 0.0f);
  }

  Vector3 CalculateAngularVelocity(Quaternion previous, Quaternion current, float dt, float minimumAngle) {
    Assert.IsTrue(dt > 0.0f);
    var rotation = current * Quaternion.Inverse(previous);
    var theta = (float)(2.0f * Math.Acos(rotation.w));

    if (float.IsNaN(theta)) return Vector3.zero;
    if (Math.Abs(theta) < minimumAngle) return Vector3.zero;

    if (theta > Math.PI)
      theta -= 2.0f * (float)Math.PI;

    var cone = (float)Math.Sqrt(rotation.x * rotation.x
      + rotation.y * rotation.y
      + rotation.z * rotation.z);

    var speed = theta / dt / cone;

    var velocity = new Vector3(
      speed * rotation.x, 
      speed * rotation.y, 
      speed * rotation.z);

    Assert.IsFalse(float.IsNaN(velocity.x));
    Assert.IsFalse(float.IsNaN(velocity.y));
    Assert.IsFalse(float.IsNaN(velocity.z));

    return velocity;
  }

  public bool GetAvatarState(out AvatarState s) {
    PoseFrame frame;
    if (!avatar.Driver.GetCurrentPose(out frame)) {
      s = AvatarState.Default;
      return false;
    }
    AvatarState.Initialize(out s, context.GetClientId(), frame, leftHand.grip, rightHand.grip);

    return true;
  }

  public void CubeAttached(ref HandData h) => UpdateHeldObject(ref h);

  public void CubeDetached(ref HandData h) {
    var rigidBody = h.grip.GetComponent<Rigidbody>();
    rigidBody.isKinematic = false;
    h.grip.layer = contextLayerCubes;
    Release(ref h, rigidBody, h.hasReleaseVelocity);
    h.grip.transform.SetParent(null);
    h.grip = null;

    if (rigidBody.position.y < MinimumCubeHeight) {
      var position = rigidBody.position;
      position.y = MinimumCubeHeight;
      rigidBody.position = position;
    }

    if (h.supports != null) {
      WakeUpObjects(h.supports);
      h.supports = null;
    }

    h.point = null;
    h.pointFrame = 0;
    h.releaseFrame = context.GetRenderFrame();
  }

  public void ResetHand(ref HandData h) {
    h.hasReleaseVelocity = true;
    TransitionToState(ref h, HandState.Neutral);
    h.hasReleaseVelocity = false;
    h.isTouching = false;
    h.line = null;
    h.point = null;
    h.pointFrame = 0;
    h.grip = null;
    h.inputFrame = 0;
    h.startFrame = 0;
    h.prevHandPosition = Vector3.zero;
    h.prevGripPosition = Vector3.zero;
    h.prevHandRotation = Quaternion.identity;
    h.prevGripRotation = Quaternion.identity;
    h.releaseFrame = 0;
    h.supports = null;
  }

  public bool IsPressingGrip() => leftHand.input.handTrigger > GripThreshold || rightHand.input.handTrigger > GripThreshold;
  public bool IsPressingIndex() => leftHand.input.indexTrigger > IndexThreshold || rightHand.input.indexTrigger > IndexThreshold;
  public bool IsPressingX() => leftHand.input.x || rightHand.input.x;
  public bool IsPressingY() => leftHand.input.y || rightHand.input.y;
  public override bool GetCurrentPose(out PoseFrame pose) => base.GetCurrentPose(out pose);
}