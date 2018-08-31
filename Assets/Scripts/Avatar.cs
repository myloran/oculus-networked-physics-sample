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
    avatar = GetComponent<OvrAvatar>();
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
    Pose p;
    if (!GetPose(out p)) return;

    UpdateHand(ref leftHand, p);
    UpdateHand(ref rightHand, p);
  }

  void FixedUpdate() {
    Assert.IsNotNull(context);
    Pose p;
    if (!GetPose(out p)) return;

    UpdateHandFixed(ref leftHand, p);
    UpdateHandFixed(ref rightHand, p);
  }

  void UpdateHand(ref HandData d, Pose pose) {
    var hand = (d.id == LeftHand) ? pose.handLeftPose : pose.handRightPose;
    var controller = (d.id == LeftHand) ? pose.controllerLeftPose : pose.controllerRightPose;

    CollectInput(ref hand, ref controller, ref d.input); //why this is needed?
    UpdateRotate(ref d);
    UpdateZoom(ref d);
    UpdateGrip(ref d);
    DetectStateChanges(ref d);
    UpdateCurrentState(ref d);
    UpdateHeldObject(ref d);
  }

  void UpdateHandFixed(ref HandData d, Pose pose) => UpdateSnapToHand(ref d);

  void UpdateRotate(ref HandData d) {
    if (!d.grip) return;

    var angle = 0.0f;

    if (d.input.stick.x <= -StickThreshold)
      angle = +RotateSpeed * Time.deltaTime;

    if (d.input.stick.x >= +StickThreshold)
      angle = -RotateSpeed * Time.deltaTime;

    d.grip.transform.RotateAround(
      d.grip.transform.position,
      d.transform.forward, 
      angle);
  }

  void UpdateZoom(ref HandData d) {
    if (!d.grip) return;

    Vector3 start, direction;
    GetFingerInput(ref d, out start, out direction);
    var position = d.grip.transform.position;

    if (d.input.stick.y <= -StickThreshold) {
      var delta = position - start; //zoom in: sneaky trick, pull center of mass towards hand on zoom in!
      var distance = delta.magnitude;

      if (distance > ZoomMinimum) {
        distance -= ZoomSpeed * Time.deltaTime;

        if (distance < ZoomMinimum)
          distance = ZoomMinimum;

        d.grip.transform.position = start + delta;
      }
    }

    if (d.input.stick.y >= +StickThreshold) {      
      if (Vector3.Dot(position - start, direction) < ZoomMaximum) //zoom out: push out strictly along point direction. this lets objects grabbed up close always zoom out in a consistent direction
        d.grip.transform.position += (ZoomSpeed * Time.deltaTime) * direction;
    }
  }

  void UpdateSnapToHand(ref HandData d) {
    if (!d.grip) return;

    Vector3 start, direction;
    GetFingerInput(ref d, out start, out direction);

    if (d.input.indexTrigger < IndexThreshold
      || d.input.indexPressFrame + IndexStickyFrames < context.GetRenderFrame()
    ) return;

    d.input.indexPressFrame = 0;
    var delta = d.grip.transform.position - start; //warp to hand on index grip
    var distance = delta.magnitude;

    if (distance > WarpDistance) {
      distance = WarpDistance;

      if (distance < ZoomMinimum)
        distance = ZoomMinimum;

      var rigidBody = d.grip.GetComponent<Rigidbody>();
      var network = d.grip.GetComponent<NetworkInfo>();
      network.SmoothMove(start + delta, rigidBody.rotation);
    }

    for (int i = 0; i < ThrowRingBufferSize; ++i) { //clear the throw ring buffer
      d.throws[i].valid = false;
      d.throws[i].speed = 0.0f;
    }
  }

  void UpdateGrip(ref HandData d) {
    if (d.input.handTrigger <= GripThreshold)
      d.inputFrame = 0;

    else if (d.inputFrame == 0)
      d.inputFrame = context.GetRenderFrame();
  }

  void UpdateHeldObject(ref HandData d) {
    if (!d.grip) return;

    int index = (d.throwIndex++) % ThrowRingBufferSize; //track data to improve throw release in ring buffer
    var diff = d.grip.transform.position - d.prevGripPosition;
    var speed = (float)Math.Sqrt(diff.x * diff.x + diff.z * diff.z);
    d.throws[index].valid = true;
    d.throws[index].speed = speed * Constants.RenderFrameRate;

    //track previous positions and rotations for hand and index finger so we can use this to determine linear and angular velocity at time of release
    d.prevHandPosition = d.transform.position;
    d.prevHandRotation = d.transform.rotation;

    d.prevGripPosition = d.grip.transform.position;
    d.prevGripRotation = d.grip.transform.rotation;

    //while an object is held set its last interaction frame to the current sim frame. this is used to boost priority for this object when it is thrown.
    var network = d.grip.GetComponent<NetworkInfo>();
    network.SetLastFrame((long)context.GetSimulationFrame());
  }

  void CollectInput(ref HandPose hand, ref ControllerPose controller, ref HandInput i) {
    i.handTrigger = hand.gripFlex;
    i.previousIndexTrigger = i.indexTrigger;
    i.indexTrigger = hand.indexFlex;

    if (i.indexTrigger >= IndexThreshold && i.previousIndexTrigger < IndexThreshold)
      i.indexPressFrame = context.GetRenderFrame();

    i.pointing = true;
    i.x = controller.button1IsDown;
    i.y = controller.button2IsDown;
    i.stick = controller.joystickPosition;
  }

  void DetectStateChanges(ref HandData d) {
    if (d.state == HandState.Neutral) {
      if (DetectGrip(ref d)) return;

      if (d.input.pointing) {
        Transition(ref d, HandState.Pointing);
        return;
      }

    } else if(d.state == HandState.Pointing) {
      if (DetectGrip(ref d))return;

      if (!d.input.pointing) {
        Transition(ref d, HandState.Neutral);
        return;
      }

    } else if (d.state == HandState.Grip) {
      if (d.input.handTrigger >= GripThreshold) return;

      if (d.input.pointing)
        Transition(ref d, HandState.Pointing);
      else
        Transition(ref d, HandState.Neutral);
    }
  }

  bool DetectGrip(ref HandData d) {
    if (d.state == HandState.Grip && d.grip == null) {
      Transition(ref d, HandState.Neutral);
      return true;
    }

    if (d.input.handTrigger < GripThreshold) return false;
    if (d.point || d.pointFrame + PointStickyFrames < context.GetRenderFrame()) return false;

    var network = d.point.GetComponent<NetworkInfo>();
    if (!network.CanGrabCube(d.inputFrame)) return false;

    AttachToHand(ref d, d.point);
    Transition(ref d, HandState.Grip);

    return true;
  }

  void AttachToHand(ref HandData d, GameObject gameObject) {
    var network = gameObject.GetComponent<NetworkInfo>();
    network.AttachCubeToLocalPlayer(this, d);

#if DEBUG_AUTHORITY
        Debug.Log( "client " + context.GetClientIndex() + " grabbed cube " + network.GetCubeId() + " and set ownership sequence to " + network.GetOwnershipSequence() );
#endif // #if DEBUG_AUTHORITY

    if (!context.IsServer())
      network.ClearConfirmed();
    else
      network.SetConfirmed();

    for (int i = 0; i < ThrowRingBufferSize; ++i) {
      d.throws[i].valid = false;
      d.throws[i].speed = 0.0f;
    }
  }

  bool IsCloseGrip(ref HandData d) {
    if (!d.grip) return false;

    var delta = d.grip.transform.position - d.transform.position;

    return delta.magnitude <= GrabDistance;
  }

  bool IsThrowing(ref HandData d) {
    int count = 0;
    var sum = 0.0f;

    for (int i = 0; i < ThrowRingBufferSize; ++i) {
      if (d.throws[i].valid)
        sum += d.throws[i].speed;

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

  void WakeUpObjects(List<GameObject> objects) {
    foreach (var obj in objects) {
      var network = obj.GetComponent<NetworkInfo>();
      context.ResetBuffer(network.GetCubeId());

      if (network.GetAuthorityId() == 0)
        context.TakeAuthority(network);

      obj.GetComponent<Rigidbody>().WakeUp();
    }
  }

  void DetachFromHand(ref HandData d) {
    if (d.grip == null) return; //IMPORTANT: This happens when passing a cube from hand-to-hand

    var network = d.grip.GetComponent<NetworkInfo>();
    network.DetachCube();

#if DEBUG_AUTHORITY
        Debug.Log( "client " + context.GetClientIndex() + " released cube " + network.GetCubeId() + ". ownership sequence is " + network.GetOwnershipSequence() + ", authority sequence is " + network.GetAuthoritySequence() );
#endif // #if DEBUG_AUTHORITY
  }

  void Transition(ref HandData h, HandState state) {
    ExitState(ref h);
    EnterState(ref h, state);
  }

  void ExitState(ref HandData d) {
    if (d.state == HandState.Pointing)
      DestroyLine(ref d);

    else if (d.state == HandState.Grip)
      DetachFromHand(ref d);
  }

  void EnterState(ref HandData d, HandState state) {
    if (state == HandState.Pointing)
      CreatePointingLine(ref d);

    d.state = state;
  }

  void UpdateCurrentState(ref HandData d) {
    if (d.state == HandState.Pointing) {
      UpdateLine(ref d);
      ForcePointAnimation(ref d);

    } else if (d.state == HandState.Grip) {
      if (IsCloseGrip(ref d))
        ForceGripAnimation(ref d);
      else
        ForcePointAnimation(ref d);

      if (!d.grip) return;
      if (d.grip.transform.position.y > 0.0f) return;

      var position = d.grip.transform.position;
      position.y = 0.0f;
      d.grip.transform.position = position;
    }
  }

  void SetPointObject(ref HandData d, GameObject gameObject) {
    d.point = gameObject;
    d.pointFrame = context.GetRenderFrame();
  }

  void CreatePointingLine(ref HandData d) {
    if (d.line) return;

    d.line = Instantiate(linePrefab, Vector3.zero, Quaternion.identity);
    Assert.IsNotNull(d.line);
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

  void GetFingerInput(ref HandData d, out Vector3 start, out Vector3 direction) {
    var positions = new[] {
      new Vector3(-0.05f, 0.0f, 0.0f),
      new Vector3(+0.05f, 0.0f, 0.0f)
    };
    start = d.transform.TransformPoint(positions[d.id]);
    direction = d.transform.forward;
  }

  void UpdateLine(ref HandData d) {
    Assert.IsNotNull(d.line);
    Vector3 start, direction;
    GetFingerInput(ref d, out start, out direction);

    var finish = start + direction * RaycastDistance;
    
    if (d.releaseFrame + PostReleaseDisableSelectFrames < context.GetRenderFrame()) { //don't allow any selection for a few frames after releasing an object      
      var colliders = Physics.OverlapSphere(d.transform.position, GrabRadius, contextLayerMask); //first select any object overlapping the hand

      if (colliders.Length > 0 && FilterPointObject(ref d, colliders[0].attachedRigidbody)) {
        finish = start;
        SetPointObject(ref d, colliders[0].gameObject);
        d.isTouching = true;

      } else {       
        d.isTouching = false; //otherwise, raycast forward along point direction for accurate selection up close
        RaycastHit hit;

        if (Physics.Linecast(start, finish, out hit, contextLayerMask)) {
          if (FilterPointObject(ref d, hit.rigidbody)) {
            finish = start + direction * hit.distance;
            SetPointObject(ref d, hit.rigidbody.gameObject);
          }

        } else if (Physics.SphereCast(start + direction * PointSphereCastStart, PointSphereCastRadius, finish, out hit, contextLayerMask)) {
          // failing an accurate hit, sphere cast starting from a bit further away to provide easier selection of far away objects
          if (FilterPointObject(ref d, hit.rigidbody)) {
            finish = start + direction * (PointSphereCastStart + hit.distance);
            SetPointObject(ref d, hit.rigidbody.gameObject);
          }
        }
      }
    }

    var line = d.line.GetComponent<LineRenderer>();
    if (!line) return;

    line.positionCount = 2;
    line.SetPosition(0, start);
    line.SetPosition(1, finish);
    line.startWidth = LineWidth;
    line.endWidth = LineWidth;
  }

  void DestroyLine(ref HandData d) {
    Assert.IsNotNull(d.line);
    Destroy(d.line);
    d.line = null;
  }

  void ForcePointAnimation(ref HandData d) {
    if (d.isTouching && d.state == HandState.Pointing)
      d.animator.SetLayerWeight(d.animator.GetLayerIndex("Point Layer"), 0.0f); //indicates state of touching an object to player (for up-close grip)
    else
      d.animator.SetLayerWeight(d.animator.GetLayerIndex("Point Layer"), 1.0f);

    d.animator.SetLayerWeight(d.animator.GetLayerIndex("Thumb Layer"), 0.0f);
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

  public bool GetAvatar(out AvatarState s) {
    Pose frame;
    if (!avatar.Driver.GetPose(out frame)) {
      s = AvatarState.Default;
      return false;
    }
    AvatarState.Initialize(out s, context.GetClientId(), frame, leftHand.grip, rightHand.grip);

    return true;
  }

  public void CubeAttached(ref HandData d) => UpdateHeldObject(ref d);

  public void CubeDetached(ref HandData d) {
    var rigidBody = d.grip.GetComponent<Rigidbody>();
    rigidBody.isKinematic = false;
    d.grip.layer = contextLayerCubes;
    Release(ref d, rigidBody, d.hasReleaseVelocity);
    d.grip.transform.SetParent(null);
    d.grip = null;

    if (rigidBody.position.y < MinimumCubeHeight) {
      var position = rigidBody.position;
      position.y = MinimumCubeHeight;
      rigidBody.position = position;
    }

    if (d.supports != null) {
      WakeUpObjects(d.supports);
      d.supports = null;
    }

    d.point = null;
    d.pointFrame = 0;
    d.releaseFrame = context.GetRenderFrame();
  }

  public void ResetHand(ref HandData d) {
    d.hasReleaseVelocity = true;
    Transition(ref d, HandState.Neutral);
    d.hasReleaseVelocity = false;
    d.isTouching = false;
    d.line = null;
    d.point = null;
    d.pointFrame = 0;
    d.grip = null;
    d.inputFrame = 0;
    d.startFrame = 0;
    d.prevHandPosition = Vector3.zero;
    d.prevGripPosition = Vector3.zero;
    d.prevHandRotation = Quaternion.identity;
    d.prevGripRotation = Quaternion.identity;
    d.releaseFrame = 0;
    d.supports = null;
  }

  public bool IsPressingGrip() => leftHand.input.handTrigger > GripThreshold || rightHand.input.handTrigger > GripThreshold;
  public bool IsPressingIndex() => leftHand.input.indexTrigger > IndexThreshold || rightHand.input.indexTrigger > IndexThreshold;
  public bool IsPressingX() => leftHand.input.x || rightHand.input.x;
  public bool IsPressingY() => leftHand.input.y || rightHand.input.y;
  public override bool GetPose(out Pose p) => base.GetPose(out p);
}